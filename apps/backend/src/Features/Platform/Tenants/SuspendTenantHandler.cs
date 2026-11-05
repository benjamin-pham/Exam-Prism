namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Domain.Tenants;
using ExamPrism.Domain.Tenants.Specifications;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;

public record SuspendTenantCommand(Guid Id);

public sealed class SuspendTenantHandler(
    CatalogDbContext db,
    ITenantCacheInvalidator cacheInvalidator,
    TimeProvider timeProvider) : IHandler<SuspendTenantCommand, TenantDetailResult>
{
    public async Task<Result<TenantDetailResult>> Handle(SuspendTenantCommand command, CancellationToken ct = default)
    {
        var tenant = await db.QueryFirstOrDefaultAsync(new TenantByIdSpec(command.Id, track: true), ct);
        if (tenant is null)
        {
            return TenantErrors.TenantNotFound();
        }

        var suspendResult = tenant.Suspend(timeProvider.GetUtcNow().UtcDateTime);
        if (suspendResult.IsFailure)
        {
            return suspendResult.Error;
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
