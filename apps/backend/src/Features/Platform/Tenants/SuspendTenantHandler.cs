namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

public record SuspendTenantCommand(Guid Id);

public sealed class SuspendTenantHandler(
    CatalogDbContext db,
    ITenantCacheInvalidator cacheInvalidator,
    TimeProvider timeProvider) : IHandler<SuspendTenantCommand, TenantDetailResult>
{
    public async Task<Result<TenantDetailResult>> Handle(SuspendTenantCommand command, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == command.Id, ct);
        if (tenant is null)
        {
            return Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.");
        }

        if (tenant.Status != TenantStatus.Active)
        {
            return Error.Conflict("INVALID_TENANT_STATUS_TRANSITION", "Only an active organization workspace can be suspended.");
        }

        tenant.Status = TenantStatus.Suspended;
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
