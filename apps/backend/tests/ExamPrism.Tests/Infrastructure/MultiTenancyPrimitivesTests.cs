namespace Tests.Infrastructure;

using System.Security.Cryptography;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Xunit;

public class MultiTenancyPrimitivesTests
{
    [Fact]
    public void TenantContext_WhenActiveTenantPresent_ExposesProperties_AndWhenNullOrNonActive_ReportsHasTenantFalse()
    {
        var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        var tenantContext = new TenantContext(accessor);

        // 1. Null context
        accessor.MultiTenantContext.Returns((IMultiTenantContext<ExamPrismTenantInfo>?)null);
        tenantContext.HasTenant.Should().BeFalse();
        tenantContext.TenantId.Should().BeNull();
        tenantContext.Identifier.Should().BeNull();
        tenantContext.Name.Should().BeNull();

        // 2. Active tenant
        var activeGuid = Guid.NewGuid();
        var activeTenantInfo = new ExamPrismTenantInfo(activeGuid.ToString("D"), "school-a")
        {
            TenantGuid = activeGuid,
            Name = "School A",
            Status = TenantStatus.Active,
            ConnectionString = "Host=localhost;Database=school_a"
        };
        var activeContext = Substitute.For<IMultiTenantContext<ExamPrismTenantInfo>>();
        activeContext.TenantInfo.Returns(activeTenantInfo);
        accessor.MultiTenantContext.Returns(activeContext);

        tenantContext.HasTenant.Should().BeTrue();
        tenantContext.TenantId.Should().Be(activeGuid);
        tenantContext.Identifier.Should().Be("school-a");
        tenantContext.Name.Should().Be("School A");

        // 3. Non-active tenants (Suspended, Provisioning, Failed)
        var nonActiveStatuses = new[] { TenantStatus.Suspended, TenantStatus.Provisioning, TenantStatus.Failed };
        foreach (var status in nonActiveStatuses)
        {
            var nonActiveGuid = Guid.NewGuid();
            var nonActiveTenantInfo = new ExamPrismTenantInfo(nonActiveGuid.ToString("D"), "school-b")
            {
                TenantGuid = nonActiveGuid,
                Name = "School B",
                Status = status,
                ConnectionString = null
            };
            var nonActiveContext = Substitute.For<IMultiTenantContext<ExamPrismTenantInfo>>();
            nonActiveContext.TenantInfo.Returns(nonActiveTenantInfo);
            accessor.MultiTenantContext.Returns(nonActiveContext);

            tenantContext.HasTenant.Should().BeFalse();
            tenantContext.TenantId.Should().BeNull();
            tenantContext.Identifier.Should().BeNull();
            tenantContext.Name.Should().BeNull();
        }
    }

    [Fact]
    public void DataProtectionConnectionStringProtector_RoundTripsSecret_ProducesCiphertextDistinctFromPlaintext_AndRejectsTamperedPayload()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "ExamPrism.Tests", Guid.NewGuid().ToString("N")));
        tempDir.Create();
        var dataProtectionProvider = DataProtectionProvider.Create(tempDir);
        var protector = new DataProtectionConnectionStringProtector(dataProtectionProvider);

        const string plaintext = "Host=db;Database=t1;Username=postgres;Password=TopSecret123!";
        var ciphertext = protector.Protect(plaintext);

        ciphertext.Should().NotBeNullOrWhiteSpace();
        ciphertext.Should().NotContain("TopSecret123!");
        ciphertext.Should().NotBe(plaintext);

        var roundTripped = protector.Unprotect(ciphertext);
        roundTripped.Should().Be(plaintext);

        var tamperedCiphertext = ciphertext[..^4] + "AAAA";
        var act = () => protector.Unprotect(tamperedCiphertext);
        act.Should().Throw<CryptographicException>();

        var blankProtect = () => protector.Protect("");
        blankProtect.Should().Throw<ArgumentException>();

        var blankUnprotect = () => protector.Unprotect("");
        blankUnprotect.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TenantConnectionStringNormalizer_AppliesConfiguredPoolLimits_PreservesEndpointCredentials_AndThrowsOnBlankInput()
    {
        var options = Options.Create(new MultiTenancyOptions
        {
            DatabasePool = new TenantDatabasePoolOptions
            {
                MaxPoolSize = 15,
                MinPoolSize = 0,
                ConnectionIdleLifetimeSeconds = 30,
                ConnectionPruningIntervalSeconds = 10
            }
        });

        var normalizer = new TenantConnectionStringNormalizer(options);

        const string raw = "Host=pg.internal;Port=5433;Database=tenant_a;Username=app;Password=pw;MaxPoolSize=100";
        var normalized = normalizer.Normalize(raw);

        var builder = new NpgsqlConnectionStringBuilder(normalized);
        builder.Pooling.Should().BeTrue();
        builder.MaxPoolSize.Should().Be(15);
        builder.MinPoolSize.Should().Be(0);
        builder.ConnectionIdleLifetime.Should().Be(30);
        builder.ConnectionPruningInterval.Should().Be(10);
        builder.Host.Should().Be("pg.internal");
        builder.Port.Should().Be(5433);
        builder.Database.Should().Be("tenant_a");
        builder.Username.Should().Be("app");
        builder.Password.Should().Be("pw");

        var blankAct = () => normalizer.Normalize("   ");
        blankAct.Should().Throw<ArgumentException>();
    }
}
