namespace ExamPrism.Infrastructure.Persistence;

using System;
using Common.MultiTenancy;
using Common.Persistence;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using ExamPrism.Infrastructure.Persistence.Interceptors;
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
        DapperTypeHandlers.EnsureRegistered();
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));
        services.AddSingleton<SlowQueryTelemetryInterceptor>();
        services.AddSingleton<AuditableEntityInterceptor>();
        services.AddScoped<DomainEventDispatchInterceptor>();
        services.AddSingleton<TenantDataSourceRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<TenantDataSourceRegistry>());

        var catalogConnection = configuration.GetConnectionString("CatalogConnection")
            ?? throw new InvalidOperationException("Connection string 'CatalogConnection' not found.");

        services.AddDbContext<CatalogDbContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            var interceptor = sp.GetRequiredService<SlowQueryTelemetryInterceptor>();
            var auditableInterceptor = sp.GetRequiredService<AuditableEntityInterceptor>();
            var dispatchInterceptor = sp.GetRequiredService<DomainEventDispatchInterceptor>();

            options.AddInterceptors(interceptor, auditableInterceptor, dispatchInterceptor);
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
            var auditableInterceptor = sp.GetRequiredService<AuditableEntityInterceptor>();
            var dispatchInterceptor = sp.GetRequiredService<DomainEventDispatchInterceptor>();

            var normalized = normalizer.Normalize(tenantInfo.ConnectionString);
            var dataSource = registry.GetOrCreate(normalized);

            options.AddInterceptors(interceptor, auditableInterceptor, dispatchInterceptor);
            options.UseNpgsql(dataSource, npgsqlOptions =>
                ConfigureNpgsql(npgsqlOptions, persistenceOptions));
        });

        services.AddScoped<ICatalogDbContext>(sp => sp.GetRequiredService<CatalogDbContext>());
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

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
