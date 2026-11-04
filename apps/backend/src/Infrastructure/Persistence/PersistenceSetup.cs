namespace ExamPrism.Infrastructure.Persistence;

using Common.MultiTenancy;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public static class PersistenceSetup
{
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecksBuilder)
    {
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));
        services.AddSingleton<SlowQueryTelemetryInterceptor>();

        var catalogConnection = configuration.GetConnectionString("CatalogConnection")
            ?? throw new InvalidOperationException("Connection string 'CatalogConnection' not found.");

        services.AddDbContext<CatalogDbContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            var interceptor = sp.GetRequiredService<SlowQueryTelemetryInterceptor>();

            options.AddInterceptors(interceptor);
            options.UseNpgsql(catalogConnection, npgsqlOptions =>
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
            });
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
            var persistenceOptions = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            var interceptor = sp.GetRequiredService<SlowQueryTelemetryInterceptor>();

            options.AddInterceptors(interceptor);
            options.UseNpgsql(normalizer.Normalize(tenantInfo.ConnectionString), npgsqlOptions =>
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
            });
        });

        healthChecksBuilder.AddDbContextCheck<CatalogDbContext>(name: "catalog_db");

        return services;
    }
}
