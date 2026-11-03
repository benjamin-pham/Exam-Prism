namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

public record MigrateSingleTenantCommand(Guid Id);

public sealed class MigrateSingleTenantHandler(
    CatalogDbContext db,
    ITenantFleetMigrator fleetMigrator) : IHandler<MigrateSingleTenantCommand, TenantMigrationItemResult>
{
    public async Task<Result<TenantMigrationItemResult>> Handle(MigrateSingleTenantCommand command, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == command.Id, ct);
        if (tenant is null)
        {
            return Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.");
        }

        if (tenant.Status != TenantStatus.Active || string.IsNullOrWhiteSpace(tenant.EncryptedConnectionString))
        {
            return Error.Conflict("INVALID_TENANT_STATUS_TRANSITION", "Only an active organization workspace with a configured database connection can be migrated.");
        }

        var result = await fleetMigrator.MigrateTenantAsync(command.Id, ct);
        return result;
    }
}
