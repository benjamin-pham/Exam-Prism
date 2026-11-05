namespace ExamPrism.Features.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using Dapper;
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
    private const string CheckSlugSql = """
        SELECT EXISTS (
            SELECT 1
            FROM "Tenants"
            WHERE "Slug" = @Slug
        );
        """;

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

        var connection = db.Database.GetDbConnection();
        var isTaken = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                CheckSlugSql,
                new { Slug = slug },
                cancellationToken: ct));

        if (isTaken)
        {
            return new CheckTenantSlugResult(slug, false, "SLUG_ALREADY_TAKEN");
        }

        return new CheckTenantSlugResult(slug, true, null);
    }
}
