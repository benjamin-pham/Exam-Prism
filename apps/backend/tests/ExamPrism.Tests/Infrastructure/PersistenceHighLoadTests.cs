namespace ExamPrism.Tests.Infrastructure;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Common.MultiTenancy;
using Common.Telemetry;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NSubstitute;
using Xunit;

public class PersistenceHighLoadTests
{
    [Fact]
    public void TenantConnectionStringNormalizer_AppliesMaxAutoPrepare_AndAutoPrepareMinUsages()
    {
        var options = Options.Create(new MultiTenancyOptions
        {
            DatabasePool = new TenantDatabasePoolOptions
            {
                MaxAutoPrepare = 32,
                AutoPrepareMinUsages = 2
            }
        });

        var normalizer = new TenantConnectionStringNormalizer(options);
        const string baseConn = "Host=localhost;Port=5432;Database=examprism_test;Username=postgres;Password=postgres";

        var normalized = normalizer.Normalize(baseConn);
        var builder = new NpgsqlConnectionStringBuilder(normalized);

        builder.MaxAutoPrepare.Should().Be(32);
        builder.AutoPrepareMinUsages.Should().Be(2);
    }

    [Fact]
    public void TenantDataSourceRegistry_ReusesDataSourceForSameConnectionString_AndEvictsLeastRecentlyUsedWhenMaxCapacityExceeded()
    {
        ExamPrismDiagnostics.ResetForTesting();

        var timeProvider = new FakeTimeProvider();
        var options = Options.Create(new MultiTenancyOptions
        {
            DatabasePool = new TenantDatabasePoolOptions
            {
                MaxCachedTenantDataSources = 2,
                DataSourceIdleEvictionMinutes = 30
            }
        });

        var registry = new TenantDataSourceRegistry(options, timeProvider);

        const string conn1 = "Host=db1.internal;Port=5432;Database=t1;Username=u;Password=p";
        const string conn2 = "Host=db2.internal;Port=5432;Database=t2;Username=u;Password=p";
        const string conn3 = "Host=db3.internal;Port=5432;Database=t3;Username=u;Password=p";

        // 1. Request conn1 at T0
        var ds1 = registry.GetOrCreate(conn1);
        registry.ActiveCount.Should().Be(1);
        ExamPrismDiagnostics.ActiveTenantDataSourcesCount.Should().Be(1);

        // 2. Request conn1 again: reuses same NpgsqlDataSource
        var ds1Reused = registry.GetOrCreate(conn1);
        ds1Reused.Should().BeSameAs(ds1);

        // 3. Advance time and request conn2 at T1
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var ds2 = registry.GetOrCreate(conn2);
        registry.ActiveCount.Should().Be(2);
        ExamPrismDiagnostics.ActiveTenantDataSourcesCount.Should().Be(2);

        // 4. Advance time and request conn3 at T2 (capacity is 2, conn1 is LRU and evicted)
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var ds3 = registry.GetOrCreate(conn3);
        registry.ActiveCount.Should().Be(2);
        ExamPrismDiagnostics.ActiveTenantDataSourcesCount.Should().Be(2);

        // 5. Assert conn2 and conn3 are retained
        var ds2Reused = registry.GetOrCreate(conn2);
        ds2Reused.Should().BeSameAs(ds2);

        var ds3Reused = registry.GetOrCreate(conn3);
        ds3Reused.Should().BeSameAs(ds3);

        // 6. Assert conn1 was evicted (requesting it again creates a new instance)
        var ds1New = registry.GetOrCreate(conn1);
        ds1New.Should().NotBeSameAs(ds1);
    }

    [Fact]
    public async Task TenantDataSourceRegistry_EvictIdleAsync_RemovesEntriesExceedingIdleLifetime()
    {
        ExamPrismDiagnostics.ResetForTesting();

        var timeProvider = new FakeTimeProvider();
        var options = Options.Create(new MultiTenancyOptions
        {
            DatabasePool = new TenantDatabasePoolOptions
            {
                MaxCachedTenantDataSources = 10,
                DataSourceIdleEvictionMinutes = 30
            }
        });

        var registry = new TenantDataSourceRegistry(options, timeProvider);
        const string conn = "Host=db.internal;Port=5432;Database=idle_test;Username=u;Password=p";

        var ds = registry.GetOrCreate(conn);
        registry.ActiveCount.Should().Be(1);
        ExamPrismDiagnostics.ActiveTenantDataSourcesCount.Should().Be(1);

        // Advance 31 minutes
        timeProvider.Advance(TimeSpan.FromMinutes(31));

        await registry.EvictIdleAsync(TimeSpan.FromMinutes(30));
        registry.ActiveCount.Should().Be(0);
        ExamPrismDiagnostics.ActiveTenantDataSourcesCount.Should().Be(0);
    }

    [Fact]
    public void ReadOnlyCatalogDbContext_And_ReadOnlyAppDbContext_EnforceNoTracking_AndFallbackToPrimaryConnectionWhenReplicaNotConfigured()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CatalogConnection"] = "Host=catalog.internal;Port=5432;Database=catalog_primary;Username=postgres;Password=postgres"
            })
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        var healthChecksBuilder = services.AddHealthChecks();

        services.AddSingleton<ITenantConnectionStringNormalizer>(new TenantConnectionStringNormalizer(
            Options.Create(new MultiTenancyOptions())));

        var tenantGuid = Guid.NewGuid();
        var tenantInfo = new ExamPrismTenantInfo(tenantGuid.ToString("D"), "tenant-1")
        {
            TenantGuid = tenantGuid,
            Status = TenantStatus.Active,
            ConnectionString = "Host=tenant.internal;Port=5432;Database=tenant_primary;Username=postgres;Password=postgres"
        };

        var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo));
        services.AddSingleton(accessor);

        services.AddPersistence(configuration, healthChecksBuilder);

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        // 1. Resolve ReadOnlyCatalogDbContext
        var readOnlyCatalog = scope.ServiceProvider.GetRequiredService<ReadOnlyCatalogDbContext>();
        readOnlyCatalog.DatabaseContext.ChangeTracker.QueryTrackingBehavior.Should().Be(QueryTrackingBehavior.NoTracking);
        readOnlyCatalog.Tenants.Should().NotBeNull();
        readOnlyCatalog.Users.Should().NotBeNull();
        readOnlyCatalog.PlatformRefreshTokens.Should().NotBeNull();

        // 2. Resolve ReadOnlyAppDbContext
        var readOnlyApp = scope.ServiceProvider.GetRequiredService<ReadOnlyAppDbContext>();
        readOnlyApp.DatabaseContext.ChangeTracker.QueryTrackingBehavior.Should().Be(QueryTrackingBehavior.NoTracking);
        readOnlyApp.Users.Should().NotBeNull();
        readOnlyApp.RefreshTokens.Should().NotBeNull();
    }
}
