namespace ExamPrism.Infrastructure.Persistence;

using System;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

public static class PersistenceSetup
{
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecksBuilder)
    {
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));
        services.AddSingleton<SlowQueryTelemetryInterceptor>();
        services.AddSingleton<TenantDataSourceRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<TenantDataSourceRegistry>());

        var catalogConnection = configuration.GetConnectionString("CatalogConnection")
            ?? throw new InvalidOperationException("Connection string 'CatalogConnection' not found.");

        services.AddDbContext<CatalogDbContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            var interceptor = sp.GetRequiredService<SlowQueryTelemetryInterceptor>();

            options.AddInterceptors(interceptor);
            options.UseNpgsql(catalogConnection, npgsqlOptions =>
                ConfigureNpgsql(npgsqlOptions, persistenceOptions));
        });

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var tenantInfo = sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>()
                .MultiTenantContext?.TenantInfo;

            if (tenantInfo is null || tenantInfo.Status != TenantStatus.Active || string.IsNullOrWhiteSpace(tenantInfo.ConnectionString))
            {
                throw new InvalidOperationException(
                    "An active tenant context with a valid connection string is required to resolve AppDbContext.");
            }

            var normalizer = sp.GetRequiredService<ITenantConnectionStringNormalizer>();
            var registry = sp.GetRequiredService<TenantDataSourceRegistry>();
            var persistenceOptions = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            var interceptor = sp.GetRequiredService<SlowQueryTelemetryInterceptor>();

            var normalized = normalizer.Normalize(tenantInfo.ConnectionString);
            var dataSource = registry.GetOrCreate(normalized);

            options.AddInterceptors(interceptor);
            options.UseNpgsql(dataSource, npgsqlOptions =>
                ConfigureNpgsql(npgsqlOptions, persistenceOptions));
        });

        services.AddScoped<ReadOnlyCatalogDbContext>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var persistenceOptions = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            var interceptor = sp.GetRequiredService<SlowQueryTelemetryInterceptor>();
            var registry = sp.GetRequiredService<TenantDataSourceRegistry>();
            var normalizer = sp.GetService<ITenantConnectionStringNormalizer>();

            var defaultConnection = configuration.GetConnectionString("CatalogConnection")
                ?? throw new InvalidOperationException("Connection string 'CatalogConnection' not found.");

            var readConnection = !string.IsNullOrWhiteSpace(persistenceOptions.CatalogReadConnection)
                ? persistenceOptions.CatalogReadConnection
                : defaultConnection;

            var normalized = normalizer != null ? normalizer.Normalize(readConnection) : readConnection;
            var dataSource = registry.GetOrCreate(normalized);

            var builder = new DbContextOptionsBuilder<CatalogDbContext>();
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            builder.AddInterceptors(interceptor);
            builder.UseNpgsql(dataSource, npgsqlOptions =>
                ConfigureNpgsql(npgsqlOptions, persistenceOptions));

            var inner = new CatalogDbContext(builder.Options);
            return new ReadOnlyCatalogDbContext(inner);
        });

        services.AddScoped<ReadOnlyAppDbContext>(sp =>
        {
            var tenantInfo = sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>()
                .MultiTenantContext?.TenantInfo;

            if (tenantInfo is null || tenantInfo.Status != TenantStatus.Active || string.IsNullOrWhiteSpace(tenantInfo.ConnectionString))
            {
                throw new InvalidOperationException(
                    "An active tenant context with a valid connection string is required to resolve ReadOnlyAppDbContext.");
            }

            var normalizer = sp.GetRequiredService<ITenantConnectionStringNormalizer>();
            var registry = sp.GetRequiredService<TenantDataSourceRegistry>();
            var persistenceOptions = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            var interceptor = sp.GetRequiredService<SlowQueryTelemetryInterceptor>();

            var normalized = normalizer.NormalizeForReadReplica(tenantInfo.ConnectionString, persistenceOptions.TenantReadReplicaHost);
            var dataSource = registry.GetOrCreate(normalized);

            var builder = new DbContextOptionsBuilder<AppDbContext>();
            builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            builder.AddInterceptors(interceptor);
            builder.UseNpgsql(dataSource, npgsqlOptions =>
                ConfigureNpgsql(npgsqlOptions, persistenceOptions));

            var inner = new AppDbContext(builder.Options);
            return new ReadOnlyAppDbContext(inner);
        });

        healthChecksBuilder.AddDbContextCheck<CatalogDbContext>(name: "catalog_db");

        return services;
    }

    internal static void ConfigureNpgsql(
        NpgsqlDbContextOptionsBuilder npgsqlOptions,
        PersistenceOptions persistenceOptions)
    {
        npgsqlOptions.CommandTimeout(persistenceOptions.CommandTimeoutSeconds);
        npgsqlOptions.MaxBatchSize(persistenceOptions.MaxBatchSize);
        if (persistenceOptions.MaxRetryCount > 0)
        {
            npgsqlOptions.EnableRetryOnFailure(
                maxRetryCount: persistenceOptions.MaxRetryCount,
                maxRetryDelay: TimeSpan.FromSeconds(persistenceOptions.MaxRetryDelaySeconds),
                errorCodesToAdd: null);
        }
    }
}
