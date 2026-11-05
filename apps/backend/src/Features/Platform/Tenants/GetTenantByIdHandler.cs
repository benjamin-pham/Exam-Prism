namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.Results;
using ExamPrism.Domain.Tenants;
using ExamPrism.Infrastructure.Persistence;

public record GetTenantByIdQuery(Guid Id);

public sealed class GetTenantByIdHandler(ReadOnlyCatalogDbContext db) : IHandler<GetTenantByIdQuery, TenantDetailResult>
{
    private const string GetTenantByIdSql = """
        SELECT "Id", "Slug", "Name", "Status", "FailureReason", "CreatedAt", "ProvisionedAt", "UpdatedAt"
        FROM "Tenants"
        WHERE "Id" = @Id
        LIMIT 1;
        """;

    public async Task<Result<TenantDetailResult>> Handle(GetTenantByIdQuery query, CancellationToken ct = default)
    {
        var tenant = await db.QueryFirstOrDefaultAsync<TenantDetailResult>(
            GetTenantByIdSql,
            new { query.Id },
            ct);

        if (tenant is null)
        {
            return TenantErrors.TenantNotFound();
        }

        return tenant;
    }
}
