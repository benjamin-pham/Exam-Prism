namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.Results;
using Dapper;
using ExamPrism.Domain.Tenants;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

public record GetTenantByIdQuery(Guid Id);

public sealed class GetTenantByIdHandler(CatalogDbContext db) : IHandler<GetTenantByIdQuery, TenantDetailResult>
{
    private const string GetTenantByIdSql = """
        SELECT id AS "Id", slug AS "Slug", name AS "Name", status AS "Status", failure_reason AS "FailureReason", created_at AS "CreatedAt", provisioned_at AS "ProvisionedAt", updated_at AS "UpdatedAt"
        FROM tenants
        WHERE id = @Id
        LIMIT 1;
        """;

    public async Task<Result<TenantDetailResult>> Handle(GetTenantByIdQuery query, CancellationToken ct = default)
    {
        var connection = db.Database.GetDbConnection();
        var tenant = await connection.QueryFirstOrDefaultAsync<TenantDetailResult>(
            new CommandDefinition(
                GetTenantByIdSql,
                new { query.Id },
                cancellationToken: ct));

        if (tenant is null)
        {
            return TenantErrors.TenantNotFound();
        }

        return tenant;
    }
}
