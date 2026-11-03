namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
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
    public async Task<Result<ListTenantsResult>> Handle(ListTenantsQuery query, CancellationToken ct = default)
    {
        var queryable = db.Tenants.AsNoTracking();

        if (query.Status.HasValue)
        {
            queryable = queryable.Where(t => t.Status == query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            var pattern = $"%{search}%";
            queryable = queryable.Where(t =>
                EF.Functions.ILike(t.Slug, pattern) ||
                EF.Functions.ILike(t.Name, pattern));
        }

        var totalCount = await queryable.CountAsync(ct);

        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize < 1 ? 20 : query.PageSize;

        var items = await queryable
            .OrderByDescending(t => t.CreatedAt)
            .ThenBy(t => t.Slug)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TenantSummaryItem(
                t.Id,
                t.Slug,
                t.Name,
                t.Status,
                t.CreatedAt,
                t.ProvisionedAt,
                t.UpdatedAt))
            .ToListAsync(ct);

        return new ListTenantsResult(items.AsReadOnly(), totalCount, page, pageSize);
    }
}
