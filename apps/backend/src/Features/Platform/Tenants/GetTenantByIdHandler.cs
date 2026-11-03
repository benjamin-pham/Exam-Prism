namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.Results;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

public record GetTenantByIdQuery(Guid Id);

public sealed class GetTenantByIdHandler(CatalogDbContext db) : IHandler<GetTenantByIdQuery, TenantDetailResult>
{
    public async Task<Result<TenantDetailResult>> Handle(GetTenantByIdQuery query, CancellationToken ct = default)
    {
        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == query.Id, ct);

        if (tenant is null)
        {
            return Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.");
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
