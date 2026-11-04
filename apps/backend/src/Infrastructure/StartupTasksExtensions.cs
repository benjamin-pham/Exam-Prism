namespace ExamPrism.Infrastructure;

using Common.MultiTenancy;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

public static class StartupTasksExtensions
{
    public static async Task InitializeInfrastructureAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        var platformAdminOptions = app.Services.GetRequiredService<IOptions<PlatformAdminOptions>>().Value;
        if (platformAdminOptions.SeedOnStartup)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var seeder = scope.ServiceProvider.GetRequiredService<PlatformAdminSeeder>();
                await seeder.SeedAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "PlatformAdminSeeder skipped on startup because database is not available or not yet migrated.");
            }
        }

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

