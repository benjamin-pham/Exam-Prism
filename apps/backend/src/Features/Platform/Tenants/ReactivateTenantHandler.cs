namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

public record ReactivateTenantCommand(Guid Id);

public sealed class ReactivateTenantHandler(
    CatalogDbContext db,
    ITenantCacheInvalidator cacheInvalidator,
    TimeProvider timeProvider) : IHandler<ReactivateTenantCommand, TenantDetailResult>
{
    public async Task<Result<TenantDetailResult>> Handle(ReactivateTenantCommand command, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == command.Id, ct);
        if (tenant is null)
        {
            return Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.");
        }

        if (tenant.Status != TenantStatus.Suspended || string.IsNullOrWhiteSpace(tenant.EncryptedConnectionString))
        {
            return Error.Conflict("INVALID_TENANT_STATUS_TRANSITION", "Only a suspended organization workspace with a valid database connection can be reactivated.");
        }

        tenant.Status = TenantStatus.Active;
        tenant.UpdatedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(ct);
        await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct);

        return new TenantDetailResult(
            tenant.Id,
            tenant.Slug,
            tenant.Name,
            tenant.Status,
            tenant.FailureReason,
            tenant.CreatedAt,
            tenant.ProvisionedAt,
            tenant.UpdatedAt);
    }
}
