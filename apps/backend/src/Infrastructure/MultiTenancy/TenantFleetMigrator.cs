namespace ExamPrism.Infrastructure.MultiTenancy;

using System.Collections.Concurrent;
using System.Diagnostics;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class TenantFleetMigrator(
    CatalogDbContext catalogDb,
    IConnectionStringProtector protector,
    ITenantConnectionStringNormalizer normalizer,
    IOptions<MultiTenancyOptions> options,
    ILogger<TenantFleetMigrator> logger,
    IServiceScopeFactory? serviceScopeFactory = null) : ITenantFleetMigrator
{
    public async Task<TenantMigrationItemResult> MigrateTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var tenant = await catalogDb.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
        {
            return new TenantMigrationItemResult(
                tenantId,
                string.Empty,
                Succeeded: false,
                AppliedMigrationsCount: 0,
                ErrorMessage: "Tenant not found.",
                DurationMs: 0);
        }

        if (string.IsNullOrWhiteSpace(tenant.EncryptedConnectionString))
        {
            return new TenantMigrationItemResult(
                tenant.Id,
                tenant.Slug,
                Succeeded: false,
                AppliedMigrationsCount: 0,
                ErrorMessage: "Tenant does not have a configured connection string.",
                DurationMs: 0);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var rawConnStr = protector.Unprotect(tenant.EncryptedConnectionString);
            var normalizedConnStr = normalizer.Normalize(rawConnStr);

            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(normalizedConnStr)
                .Options;

            await using var tenantDb = new AppDbContext(dbOptions);
            var pending = (await tenantDb.Database.GetPendingMigrationsAsync(ct)).Count();
            await tenantDb.Database.MigrateAsync(ct);
            sw.Stop();

            return new TenantMigrationItemResult(
                tenant.Id,
                tenant.Slug,
                Succeeded: true,
                AppliedMigrationsCount: pending,
                ErrorMessage: null,
                DurationMs: sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "Failed to migrate tenant database {TenantId} ({Slug})", tenant.Id, tenant.Slug);
            return new TenantMigrationItemResult(
                tenant.Id,
                tenant.Slug,
                Succeeded: false,
                AppliedMigrationsCount: 0,
                ErrorMessage: ex.Message,
                DurationMs: sw.ElapsedMilliseconds);
        }
    }

    public async Task<FleetMigrationSummaryResult> MigrateAllActiveTenantsAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var activeTenants = await catalogDb.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active && t.EncryptedConnectionString != null && t.EncryptedConnectionString != "")
            .Select(t => t.Id)
            .ToListAsync(ct);

        if (activeTenants.Count > 1)
        {
            ArgumentNullException.ThrowIfNull(serviceScopeFactory);
        }

        var maxParallelism = Math.Max(1, options.Value.FleetMigration.MaxDegreeOfParallelism);
        var results = new ConcurrentBag<TenantMigrationItemResult>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxParallelism,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(activeTenants, parallelOptions, async (tenantId, token) =>
        {
            TenantMigrationItemResult itemResult;
            if (serviceScopeFactory != null)
            {
                await using var scope = serviceScopeFactory.CreateAsyncScope();
                var scopedMigrator = scope.ServiceProvider.GetRequiredService<ITenantFleetMigrator>();
                itemResult = await scopedMigrator.MigrateTenantAsync(tenantId, token);
            }
            else
            {
                itemResult = await MigrateTenantAsync(tenantId, token);
            }

            results.Add(itemResult);
        });

        sw.Stop();

        var sortedResults = results.OrderBy(r => r.Slug, StringComparer.OrdinalIgnoreCase).ToList();
        var succeededCount = sortedResults.Count(r => r.Succeeded);
        var failedCount = sortedResults.Count(r => !r.Succeeded);

        return new FleetMigrationSummaryResult(
            TotalTenants: sortedResults.Count,
            SucceededCount: succeededCount,
            FailedCount: failedCount,
            TotalDurationMs: sw.ElapsedMilliseconds,
            Results: sortedResults);
    }
}
