namespace ExamPrism.Features.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Domain.Tenants;
using ExamPrism.Infrastructure.Persistence;

public record GetTenantProvisioningStatusQuery(string Slug);

public record GetTenantProvisioningStatusResult(
    string Slug,
    string Name,
    TenantStatus Status,
    DateTime? ProvisionedAt);

public sealed class GetTenantProvisioningStatusHandler(ReadOnlyCatalogDbContext db)
    : IHandler<GetTenantProvisioningStatusQuery, GetTenantProvisioningStatusResult>
{
    private const string GetStatusSql = """
        SELECT "Slug", "Name", "Status", "ProvisionedAt"
        FROM "Tenants"
        WHERE "Slug" = @Slug
        LIMIT 1;
        """;

    public async Task<Result<GetTenantProvisioningStatusResult>> Handle(GetTenantProvisioningStatusQuery query, CancellationToken ct = default)
    {
        var slug = (query.Slug ?? string.Empty).Trim().ToLowerInvariant();

        var tenant = await db.QueryFirstOrDefaultAsync<GetTenantProvisioningStatusResult>(
            GetStatusSql,
            new { Slug = slug },
            ct);

        if (tenant is null)
        {
            return TenantErrors.TenantNotFound();
        }

        return tenant;
    }
}
