namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
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
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == command.Id, ct);
        if (tenant is null)
        {
            return Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.");
        }

        if (tenant.Status is TenantStatus.Active or TenantStatus.Suspended)
        {
            return Error.Conflict("INVALID_TENANT_STATUS_TRANSITION", "Only a failed or provisioning organization workspace can retry provisioning.");
        }

        tenant.Status = TenantStatus.Provisioning;
        tenant.FailureReason = null;
        tenant.UpdatedAt = timeProvider.GetUtcNow();

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
