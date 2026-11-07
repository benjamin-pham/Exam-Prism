namespace ExamPrism.Modules.Tenants.MultiTenancy;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Modules;
using Common.MultiTenancy;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

public sealed class MultiTenancyStartupInitializer : IStartupInitializer
{
    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var multiTenancyOptions = services.GetService<IOptions<MultiTenancyOptions>>()?.Value;
        if (multiTenancyOptions is null)
        {
            return;
        }

        var logger = services.GetService<ILogger<MultiTenancyStartupInitializer>>();

        if (multiTenancyOptions.Provisioning.RecoverStuckProvisioningOnStartup)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
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
                logger?.LogWarning(ex, "Stuck provisioning recovery skipped on startup because database or bus is not available.");
            }
        }

        if (multiTenancyOptions.FleetMigration.MigrateOnStartup)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var fleetMigrator = scope.ServiceProvider.GetRequiredService<ITenantFleetMigrator>();
                await fleetMigrator.MigrateAllActiveTenantsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Fleet migration skipped on startup because database is not available.");
            }
        }
    }
}
