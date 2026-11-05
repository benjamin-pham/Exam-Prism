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
        SELECT "Id", "Slug", "Name", "Status", "FailureReason", "CreatedAt", "ProvisionedAt", "UpdatedAt"
        FROM "Tenants"
        WHERE "Id" = @Id
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
