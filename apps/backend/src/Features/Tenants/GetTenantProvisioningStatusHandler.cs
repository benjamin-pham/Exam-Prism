namespace ExamPrism.Features.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

public record GetTenantProvisioningStatusQuery(string Slug);

public record GetTenantProvisioningStatusResult(
    string Slug,
    string Name,
    TenantStatus Status,
    DateTimeOffset? ProvisionedAt);

public sealed class GetTenantProvisioningStatusHandler(CatalogDbContext db)
    : IHandler<GetTenantProvisioningStatusQuery, GetTenantProvisioningStatusResult>
{
    public async Task<Result<GetTenantProvisioningStatusResult>> Handle(GetTenantProvisioningStatusQuery query, CancellationToken ct = default)
    {
        var slug = (query.Slug ?? string.Empty).Trim().ToLowerInvariant();

        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (tenant is null)
        {
            return Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.");
        }

        return new GetTenantProvisioningStatusResult(
            tenant.Slug,
            tenant.Name,
            tenant.Status,
            tenant.ProvisionedAt);
    }
}
