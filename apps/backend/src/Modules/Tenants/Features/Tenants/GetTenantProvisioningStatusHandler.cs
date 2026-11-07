namespace ExamPrism.Features.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using Dapper;
using ExamPrism.Domain.Tenants;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

public record GetTenantProvisioningStatusQuery(string Slug);

public record GetTenantProvisioningStatusResult(
    string Slug,
    string Name,
    TenantStatus Status,
    DateTime? ProvisionedAt);

public sealed class GetTenantProvisioningStatusHandler(CatalogDbContext db)
    : IHandler<GetTenantProvisioningStatusQuery, GetTenantProvisioningStatusResult>
{
    private const string GetStatusSql = """
        SELECT slug AS "Slug", name AS "Name", status AS "Status", provisioned_at AS "ProvisionedAt"
        FROM tenants
        WHERE slug = @Slug
        LIMIT 1;
        """;

    public async Task<Result<GetTenantProvisioningStatusResult>> Handle(GetTenantProvisioningStatusQuery query, CancellationToken ct = default)
    {
        var slug = (query.Slug ?? string.Empty).Trim().ToLowerInvariant();

        var connection = db.Database.GetDbConnection();
        var tenant = await connection.QueryFirstOrDefaultAsync<GetTenantProvisioningStatusResult>(
            new CommandDefinition(
                GetStatusSql,
                new { Slug = slug },
                cancellationToken: ct));

        if (tenant is null)
        {
            return TenantErrors.TenantNotFound();
        }

        return tenant;
    }
}
