namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;

public record MigrateAllTenantsCommand();

public sealed class MigrateAllTenantsHandler(ITenantFleetMigrator fleetMigrator)
    : IHandler<MigrateAllTenantsCommand, FleetMigrationSummaryResult>
{
    public async Task<Result<FleetMigrationSummaryResult>> Handle(MigrateAllTenantsCommand command, CancellationToken ct = default)
    {
        var summary = await fleetMigrator.MigrateAllActiveTenantsAsync(ct);
        return summary;
    }
}
