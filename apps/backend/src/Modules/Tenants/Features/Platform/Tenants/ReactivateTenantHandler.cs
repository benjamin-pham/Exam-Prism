namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Domain.Tenants;
using ExamPrism.Domain.Tenants.Specifications;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;

public record ReactivateTenantCommand(Guid Id);

public sealed class ReactivateTenantHandler(
    CatalogDbContext db,
    ITenantCacheInvalidator cacheInvalidator,
    TimeProvider timeProvider) : IHandler<ReactivateTenantCommand, TenantDetailResult>
{
    public async Task<Result<TenantDetailResult>> Handle(ReactivateTenantCommand command, CancellationToken ct = default)
    {
        var tenant = await db.QueryFirstOrDefaultAsync(new TenantByIdSpec(command.Id, track: true), ct);
        if (tenant is null)
        {
            return TenantErrors.TenantNotFound();
        }

        var reactivateResult = tenant.Reactivate(timeProvider.GetUtcNow().UtcDateTime);
        if (reactivateResult.IsFailure)
        {
            return reactivateResult.Error;
        }

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
