namespace ExamPrism.Features.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Domain.Tenants;
using ExamPrism.Domain.Tenants.Specifications;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;

public record GetTenantProvisioningStatusQuery(string Slug);

public record GetTenantProvisioningStatusResult(
    string Slug,
    string Name,
    TenantStatus Status,
    DateTime? ProvisionedAt);

public sealed class GetTenantProvisioningStatusHandler(CatalogDbContext db)
    : IHandler<GetTenantProvisioningStatusQuery, GetTenantProvisioningStatusResult>
{
    public async Task<Result<GetTenantProvisioningStatusResult>> Handle(GetTenantProvisioningStatusQuery query, CancellationToken ct = default)
    {
        var slug = (query.Slug ?? string.Empty).Trim().ToLowerInvariant();

        var tenant = await db.QueryFirstOrDefaultAsync(new TenantBySlugSpec(slug), ct);
        if (tenant is null)
        {
            return TenantErrors.TenantNotFound();
        }

        return new GetTenantProvisioningStatusResult(
            tenant.Slug,
            tenant.Name,
            tenant.Status,
            tenant.ProvisionedAt);
    }
}
