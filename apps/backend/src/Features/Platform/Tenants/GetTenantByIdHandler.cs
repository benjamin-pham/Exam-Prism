namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.Results;
using ExamPrism.Domain.Tenants;
using ExamPrism.Domain.Tenants.Specifications;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;

public record GetTenantByIdQuery(Guid Id);

public sealed class GetTenantByIdHandler(CatalogDbContext db) : IHandler<GetTenantByIdQuery, TenantDetailResult>
{
    public async Task<Result<TenantDetailResult>> Handle(GetTenantByIdQuery query, CancellationToken ct = default)
    {
        var tenant = await db.QueryFirstOrDefaultAsync(new TenantByIdSpec(query.Id), ct);

        if (tenant is null)
        {
            return TenantErrors.TenantNotFound();
        }

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
