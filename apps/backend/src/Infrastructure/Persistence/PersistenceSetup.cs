namespace ExamPrism.Infrastructure.Persistence;

using Common.MultiTenancy;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class PersistenceSetup
{
    public static IServiceCollection AddExamPrismPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecksBuilder)
    {
        var catalogConnection = configuration.GetConnectionString("CatalogConnection")
            ?? throw new InvalidOperationException("Connection string 'CatalogConnection' not found.");

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(catalogConnection));

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
            options.UseNpgsql(normalizer.Normalize(tenantInfo.ConnectionString));
        });

        healthChecksBuilder.AddDbContextCheck<CatalogDbContext>(name: "catalog_db");

        return services;
    }
}

