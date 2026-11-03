namespace Common.MultiTenancy;

public record TenantMigrationItemResult(
    Guid TenantId,
    string Slug,
    bool Succeeded,
    int AppliedMigrationsCount,
    string? ErrorMessage,
    long DurationMs);

public record FleetMigrationSummaryResult(
    int TotalTenants,
    int SucceededCount,
    int FailedCount,
    long TotalDurationMs,
    IReadOnlyList<TenantMigrationItemResult> Results);

public interface ITenantFleetMigrator
{
    Task<TenantMigrationItemResult> MigrateTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<FleetMigrationSummaryResult> MigrateAllActiveTenantsAsync(CancellationToken ct = default);
}
