namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using Dapper;
using ExamPrism.Infrastructure.Persistence;
using FluentValidation;

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

public sealed class ListTenantsHandler(ReadOnlyCatalogDbContext db) : IHandler<ListTenantsQuery, ListTenantsResult>
{
    private const string ListTenantsSql = """
        SELECT COUNT(*)
        FROM "Tenants"
        WHERE (CAST(@Status AS varchar(32)) IS NULL OR "Status" = @Status)
          AND (CAST(@Pattern AS text) IS NULL OR "Slug" ILIKE @Pattern OR "Name" ILIKE @Pattern);

        SELECT "Id", "Slug", "Name", "Status", "CreatedAt", "ProvisionedAt", "UpdatedAt"
        FROM "Tenants"
        WHERE (CAST(@Status AS varchar(32)) IS NULL OR "Status" = @Status)
          AND (CAST(@Pattern AS text) IS NULL OR "Slug" ILIKE @Pattern OR "Name" ILIKE @Pattern)
        ORDER BY "CreatedAt" DESC, "Slug" ASC
        LIMIT @Take OFFSET @Skip;
        """;

    public async Task<Result<ListTenantsResult>> Handle(ListTenantsQuery query, CancellationToken ct = default)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 20 : query.PageSize;
        var skip = (page - 1) * pageSize;
        var status = query.Status?.ToString();
        var pattern = string.IsNullOrWhiteSpace(query.Search) ? null : $"%{query.Search.Trim()}%";

        var (totalCount, items) = await db.QueryMultipleAsync(
            ListTenantsSql,
            async grid =>
            {
                var count = await grid.ReadSingleAsync<int>();
                var rows = (await grid.ReadAsync<TenantSummaryItem>()).AsList().AsReadOnly();
                return (count, rows);
            },
            new
            {
                Status = status,
                Pattern = pattern,
                Take = pageSize,
                Skip = skip
            },
            ct);

        return new ListTenantsResult(items, totalCount, page, pageSize);
    }
}
