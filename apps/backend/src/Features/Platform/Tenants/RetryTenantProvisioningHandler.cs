namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Domain.Tenants;
using ExamPrism.Domain.Tenants.Specifications;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Wolverine;

public record RetryTenantProvisioningCommand(Guid Id);

public sealed class RetryTenantProvisioningHandler(
    CatalogDbContext db,
    ITenantCacheInvalidator cacheInvalidator,
    IMessageBus bus,
    TimeProvider timeProvider) : IHandler<RetryTenantProvisioningCommand, TenantDetailResult>
{
    public async Task<Result<TenantDetailResult>> Handle(RetryTenantProvisioningCommand command, CancellationToken ct = default)
    {
        var tenant = await db.QueryFirstOrDefaultAsync(new TenantByIdSpec(command.Id, track: true), ct);
        if (tenant is null)
        {
            return TenantErrors.TenantNotFound();
        }

        var retryResult = tenant.RetryProvisioning(timeProvider.GetUtcNow().UtcDateTime);
        if (retryResult.IsFailure)
        {
            return retryResult.Error;
        }

        await db.SaveChangesAsync(ct);
        await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct);
        await bus.SendAsync(new StartTenantProvisioningCommand(tenant.Id));

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
