namespace ExamPrism.Features.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public record CheckTenantSlugQuery(string Slug);

public record CheckTenantSlugResult(string Slug, bool IsAvailable, string? ReasonCode);

public sealed class CheckTenantSlugHandler(
    CatalogDbContext db,
    IOptions<MultiTenancyOptions> options) : IHandler<CheckTenantSlugQuery, CheckTenantSlugResult>
{
    public async Task<Result<CheckTenantSlugResult>> Handle(CheckTenantSlugQuery query, CancellationToken ct = default)
    {
        var slug = (query.Slug ?? string.Empty).Trim().ToLowerInvariant();

        if (!TenantSlugValidator.IsValid(slug))
        {
            return new CheckTenantSlugResult(slug, false, "INVALID_SLUG");
        }

        if (options.Value.ReservedSubdomains.Contains(slug, StringComparer.OrdinalIgnoreCase))
        {
            return new CheckTenantSlugResult(slug, false, "RESERVED_SLUG");
        }

        var isTaken = await db.Tenants.AsNoTracking().AnyAsync(t => t.Slug == slug, ct);
        if (isTaken)
        {
            return new CheckTenantSlugResult(slug, false, "SLUG_ALREADY_TAKEN");
        }

        return new CheckTenantSlugResult(slug, true, null);
    }
}
