namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using Dapper;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

public record ListTenantsQuery(
    int Page = 1,
    int PageSize = 20,
    TenantStatus? Status = null,
    string? Search = null);

public sealed class ListTenantsQueryValidator : AbstractValidator<ListTenantsQuery>
{
    public ListTenantsQueryValidator()
    {
        RuleFor(x => x.Page)
            .GreaterThanOrEqualTo(1)
            .WithErrorCode("INVALID_PAGE")
            .WithMessage("Page number must be greater than or equal to 1.");

        RuleFor(x => x.PageSize)
            .InclusiveBetween(1, 100)
            .WithErrorCode("INVALID_PAGE_SIZE")
            .WithMessage("Page size must be between 1 and 100.");
    }
}

public record ListTenantsResult(
    IReadOnlyList<TenantSummaryItem> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed class ListTenantsHandler(CatalogDbContext db) : IHandler<ListTenantsQuery, ListTenantsResult>
{
    private const string ListTenantsSql = """
        SELECT COUNT(*)
        FROM tenants
        WHERE (CAST(@Status AS varchar(32)) IS NULL OR status = @Status)
          AND (CAST(@Pattern AS text) IS NULL OR slug ILIKE @Pattern OR name ILIKE @Pattern);

        SELECT id AS "Id", slug AS "Slug", name AS "Name", status AS "Status", created_at AS "CreatedAt", provisioned_at AS "ProvisionedAt", updated_at AS "UpdatedAt"
        FROM tenants
        WHERE (CAST(@Status AS varchar(32)) IS NULL OR status = @Status)
          AND (CAST(@Pattern AS text) IS NULL OR slug ILIKE @Pattern OR name ILIKE @Pattern)
        ORDER BY created_at DESC, slug ASC
        LIMIT @Take OFFSET @Skip;
        """;

    public async Task<Result<ListTenantsResult>> Handle(ListTenantsQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 20 : query.PageSize;
        var skip = (page - 1) * pageSize;
        var status = query.Status?.ToString();
        var pattern = string.IsNullOrWhiteSpace(query.Search) ? null : $"%{query.Search.Trim()}%";

        var connection = db.Database.GetDbConnection();
        await using var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(
                ListTenantsSql,
                new
                {
                    Status = status,
                    Pattern = pattern,
                    Take = pageSize,
                    Skip = skip
                },
                cancellationToken: ct));

        var totalCount = await grid.ReadSingleAsync<int>();
        var items = (await grid.ReadAsync<TenantSummaryItem>()).AsList().AsReadOnly();

        return new ListTenantsResult(items, totalCount, page, pageSize);
    }
}
