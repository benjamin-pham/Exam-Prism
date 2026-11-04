namespace ExamPrism.Infrastructure.MultiTenancy;

using Common.MultiTenancy;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;
using Wolverine.FluentValidation;

public static class MultiTenancySetup
{
    public static IHostBuilder UseWolverine(this IHostBuilder hostBuilder)
    {
        return hostBuilder.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(MultiTenancySetup).Assembly);
            opts.UseFluentValidation();
            opts.Policies.AddMiddleware<TenantWolverineMiddleware>();
        });
    }

    public static IServiceCollection AddMultiTenancy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var busDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IMessageBus));
        if (busDescriptor != null)
        {
            services.Remove(busDescriptor);
            services.Add(new ServiceDescriptor(
                typeof(Wolverine.Runtime.MessageContext),
                busDescriptor.ImplementationType ?? typeof(Wolverine.Runtime.MessageContext),
                busDescriptor.Lifetime));
            services.Add(new ServiceDescriptor(
                typeof(IMessageBus),
                sp => new MultiTenantMessageBus(
                    sp.GetRequiredService<Wolverine.Runtime.MessageContext>(),
                    sp.GetRequiredService<IMultiTenantStore<ExamPrismTenantInfo>>(),
                    sp.GetRequiredService<IMultiTenantContextSetter>(),
                    sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>()),
                busDescriptor.Lifetime));
        }

        services.Configure<MultiTenancyOptions>(configuration.GetSection(MultiTenancyOptions.SectionName));
        services.AddDataProtection();
        services.AddSingleton<IConnectionStringProtector, DataProtectionConnectionStringProtector>();
        services.AddSingleton<ITenantConnectionStringNormalizer, TenantConnectionStringNormalizer>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<CatalogMultiTenantStore>();
        services.AddScoped<ITenantCacheInvalidator>(sp => sp.GetRequiredService<CatalogMultiTenantStore>());
        services.AddScoped<ITenantProvisioner>(sp => new TenantProvisioner(
            sp.GetRequiredService<CatalogDbContext>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<MultiTenancyOptions>>(),
            sp.GetRequiredService<IConnectionStringProtector>(),
            sp.GetRequiredService<ITenantConnectionStringNormalizer>(),
            sp.GetRequiredService<ITenantCacheInvalidator>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<TenantProvisioner>>()));
        services.AddScoped<ITenantFleetMigrator>(sp => new TenantFleetMigrator(
            sp.GetRequiredService<CatalogDbContext>(),
            sp.GetRequiredService<IConnectionStringProtector>(),
            sp.GetRequiredService<ITenantConnectionStringNormalizer>(),
            sp.GetRequiredService<IOptions<MultiTenancyOptions>>(),
            sp.GetRequiredService<ILogger<TenantFleetMigrator>>(),
            sp.GetRequiredService<IServiceScopeFactory>()));

        services.AddMultiTenant<ExamPrismTenantInfo>()
            .WithStrategy<SubdomainMultiTenantStrategy>(ServiceLifetime.Scoped)
            .WithStore<CatalogMultiTenantStore>(ServiceLifetime.Scoped);

        return services;
    }

    public static async Task InitializeMultiTenancyAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        var multiTenancyOptions = app.Services.GetRequiredService<IOptions<MultiTenancyOptions>>().Value;
        if (multiTenancyOptions.Provisioning.RecoverStuckProvisioningOnStartup)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
                var stuckTenants = await catalogDb.Tenants
                    .Where(t => t.Status == TenantStatus.Provisioning)
                    .Select(t => t.Id)
                    .ToListAsync(cancellationToken);

                foreach (var tenantId in stuckTenants)
                {
                    await bus.SendAsync(new StartTenantProvisioningCommand(tenantId));
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Stuck provisioning recovery skipped on startup because database or bus is not available.");
            }
        }

        if (multiTenancyOptions.FleetMigration.MigrateOnStartup)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var fleetMigrator = scope.ServiceProvider.GetRequiredService<ITenantFleetMigrator>();
                await fleetMigrator.MigrateAllActiveTenantsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Fleet migration skipped on startup because database is not available.");
            }
        }
    }
}

