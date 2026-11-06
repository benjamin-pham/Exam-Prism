namespace Common.Telemetry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

public static class ExamPrismDiagnostics
{
    public const string ServiceName = "ExamPrism";
    public const string ActivitySourceName = ServiceName;
    public const string MeterName = ServiceName;
    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    private static readonly Counter<long> SlowQueriesCounter =
        Meter.CreateCounter<long>("examprism.db.slow_queries", "{queries}", "Number of database queries exceeding the slow query threshold");

    private static readonly Counter<long> CacheBackplaneEvictionsCounter =
        Meter.CreateCounter<long>("examprism.cache.backplane_evictions", "{evictions}", "Number of L1 cache evictions triggered by Redis backplane");

    private static readonly Counter<long> IdempotencyReplaysCounter =
        Meter.CreateCounter<long>("examprism.idempotency.replays", "{replays}", "Number of idempotent requests served from cache");

    private static readonly Counter<long> IdempotencyConflictsCounter =
        Meter.CreateCounter<long>("examprism.idempotency.conflicts", "{conflicts}", "Number of concurrent idempotent requests rejected due to in-flight locks");

    private static readonly Counter<long> LoadSheddingRejectedCounter =
        Meter.CreateCounter<long>("examprism.load_shedding.rejected", "{rejections}", "Number of requests rejected by adaptive load shedding");

    private static readonly Counter<long> JwtRevokedRejectedCounter =
        Meter.CreateCounter<long>("examprism.auth.jwt_revoked_rejected", "{rejections}", "Number of authentication rejections due to revoked JWT");

    private static readonly Counter<long> PasswordHashBulkheadRejectedCounter =
        Meter.CreateCounter<long>("examprism.auth.password_hash_bulkhead_rejected", "{rejections}", "Number of password hashing requests rejected by CPU bulkhead");

    private static readonly Counter<long> BatcherFlushedItemsCounter =
        Meter.CreateCounter<long>("examprism.batcher.flushed_items", "{items}", "Number of items flushed by write-behind batchers");

    private static readonly Counter<long> IpPenaltyBoxRejectedCounter =
        Meter.CreateCounter<long>("examprism.security.ip_penalty_box_rejected", "{rejections}", "Number of requests rejected by IP penalty box");

    private static readonly Counter<long> IpBansIssuedCounter =
        Meter.CreateCounter<long>("examprism.security.ip_bans_issued", "{bans}", "Number of IP bans issued by reputation service");

    private static readonly Counter<long> DecompressionBombRejectedCounter =
        Meter.CreateCounter<long>("examprism.security.decompression_bomb_rejected", "{rejections}", "Number of requests rejected due to decompression bomb detection");

    private static readonly Counter<long> TenantCatalogShieldRejectedCounter =
        Meter.CreateCounter<long>("examprism.security.tenant_catalog_shield_rejected", "{rejections}", "Number of requests rejected by tenant catalog cold-miss shield");

    private static readonly Counter<long> SpoofedIpIgnoredCounter =
        Meter.CreateCounter<long>("examprism.security.spoofed_ip_ignored", "{ignored}", "Number of spoofed forwarded IP headers ignored from untrusted peers");

    private static readonly Counter<long> RateLimitRejectedCounter =
        Meter.CreateCounter<long>("examprism.http.rate_limit_rejected", "{rejections}", "Number of requests rejected by rate limiting");

    private static readonly ObservableGauge<long> ActiveTenantDataSourcesGauge =
        Meter.CreateObservableGauge<long>("examprism.db.tenant_datasources.active", () => ActiveTenantDataSourcesCount, "{sources}", "Number of active cached tenant NpgsqlDataSources");

    private static readonly ObservableGauge<long> InFlightRequestsGauge =
        Meter.CreateObservableGauge<long>("examprism.http.in_flight_requests", () => InFlightRequestsCount, "{requests}", "Current number of in-flight HTTP requests");

    private static long _slowQueriesCount;
    private static long _cacheBackplaneEvictionsCount;
    private static long _idempotencyReplaysCount;
    private static long _idempotencyConflictsCount;
    private static long _activeTenantDataSourcesCount;
    private static long _loadSheddingRejectedCount;
    private static long _jwtRevokedRejectedCount;
    private static long _passwordHashBulkheadRejectedCount;
    private static long _batcherFlushedItemsCount;
    private static long _inFlightRequestsCount;
    private static long _ipPenaltyBoxRejectedCount;
    private static long _ipBansIssuedCount;
    private static long _decompressionBombRejectedCount;
    private static long _tenantCatalogShieldRejectedCount;
    private static long _spoofedIpIgnoredCount;
    private static long _rateLimitRejectedCount;

    public static long SlowQueriesCount => Interlocked.Read(ref _slowQueriesCount);
    public static long CacheBackplaneEvictionsCount => Interlocked.Read(ref _cacheBackplaneEvictionsCount);
    public static long IdempotencyReplaysCount => Interlocked.Read(ref _idempotencyReplaysCount);
    public static long IdempotencyConflictsCount => Interlocked.Read(ref _idempotencyConflictsCount);
    public static long ActiveTenantDataSourcesCount => Interlocked.Read(ref _activeTenantDataSourcesCount);
    public static long LoadSheddingRejectedCount => Interlocked.Read(ref _loadSheddingRejectedCount);
    public static long JwtRevokedRejectedCount => Interlocked.Read(ref _jwtRevokedRejectedCount);
    public static long PasswordHashBulkheadRejectedCount => Interlocked.Read(ref _passwordHashBulkheadRejectedCount);
    public static long BatcherFlushedItemsCount => Interlocked.Read(ref _batcherFlushedItemsCount);
    public static long InFlightRequestsCount => Interlocked.Read(ref _inFlightRequestsCount);
    public static long IpPenaltyBoxRejectedCount => Interlocked.Read(ref _ipPenaltyBoxRejectedCount);
    public static long IpBansIssuedCount => Interlocked.Read(ref _ipBansIssuedCount);
    public static long DecompressionBombRejectedCount => Interlocked.Read(ref _decompressionBombRejectedCount);
    public static long TenantCatalogShieldRejectedCount => Interlocked.Read(ref _tenantCatalogShieldRejectedCount);
    public static long SpoofedIpIgnoredCount => Interlocked.Read(ref _spoofedIpIgnoredCount);
    public static long RateLimitRejectedCount => Interlocked.Read(ref _rateLimitRejectedCount);

    public static void RecordSlowQuery(string? commandText = null, double durationMs = 0, string? tenantId = null)
    {
        Interlocked.Increment(ref _slowQueriesCount);
        SlowQueriesCounter.Add(1,
            new KeyValuePair<string, object?>("tenant.id", tenantId ?? "platform"));
    }

    public static void RecordCacheBackplaneEviction()
    {
        Interlocked.Increment(ref _cacheBackplaneEvictionsCount);
        CacheBackplaneEvictionsCounter.Add(1);
    }

    public static void RecordIdempotencyReplay()
    {
        Interlocked.Increment(ref _idempotencyReplaysCount);
        IdempotencyReplaysCounter.Add(1);
    }

    public static void RecordIdempotencyConflict()
    {
        Interlocked.Increment(ref _idempotencyConflictsCount);
        IdempotencyConflictsCounter.Add(1);
    }

    public static void SetActiveTenantDataSources(long count)
    {
        Interlocked.Exchange(ref _activeTenantDataSourcesCount, Math.Max(0, count));
    }

    public static void RecordLoadSheddingRejection(string? priority = null)
    {
        Interlocked.Increment(ref _loadSheddingRejectedCount);
        LoadSheddingRejectedCounter.Add(1,
            new KeyValuePair<string, object?>("priority", priority ?? "unknown"));
    }

    public static void RecordJwtRevokedRejection()
    {
        Interlocked.Increment(ref _jwtRevokedRejectedCount);
        JwtRevokedRejectedCounter.Add(1);
    }

    public static void RecordPasswordHashBulkheadRejection()
    {
        Interlocked.Increment(ref _passwordHashBulkheadRejectedCount);
        PasswordHashBulkheadRejectedCounter.Add(1);
    }

    public static void RecordBatchFlushed(int itemCount)
    {
        if (itemCount <= 0) return;
        Interlocked.Add(ref _batcherFlushedItemsCount, itemCount);
        BatcherFlushedItemsCounter.Add(itemCount);
    }

    public static void RecordIpPenaltyBoxRejection()
    {
        Interlocked.Increment(ref _ipPenaltyBoxRejectedCount);
        IpPenaltyBoxRejectedCounter.Add(1);
    }

    public static void RecordIpBanIssued()
    {
        Interlocked.Increment(ref _ipBansIssuedCount);
        IpBansIssuedCounter.Add(1);
    }

    public static void RecordDecompressionBombRejection()
    {
        Interlocked.Increment(ref _decompressionBombRejectedCount);
        DecompressionBombRejectedCounter.Add(1);
    }

    public static void RecordTenantCatalogShieldRejection()
    {
        Interlocked.Increment(ref _tenantCatalogShieldRejectedCount);
        TenantCatalogShieldRejectedCounter.Add(1);
    }

    public static void RecordSpoofedIpIgnored()
    {
        Interlocked.Increment(ref _spoofedIpIgnoredCount);
        SpoofedIpIgnoredCounter.Add(1);
    }

    public static void RecordRateLimitRejection()
    {
        Interlocked.Increment(ref _rateLimitRejectedCount);
        RateLimitRejectedCounter.Add(1);
    }

    public static void IncrementInFlightRequests()
    {
        Interlocked.Increment(ref _inFlightRequestsCount);
    }

    public static void DecrementInFlightRequests()
    {
        Interlocked.Decrement(ref _inFlightRequestsCount);
    }

    public static void ResetForTesting()
    {
        Interlocked.Exchange(ref _slowQueriesCount, 0);
        Interlocked.Exchange(ref _cacheBackplaneEvictionsCount, 0);
        Interlocked.Exchange(ref _idempotencyReplaysCount, 0);
        Interlocked.Exchange(ref _idempotencyConflictsCount, 0);
        Interlocked.Exchange(ref _activeTenantDataSourcesCount, 0);
        Interlocked.Exchange(ref _loadSheddingRejectedCount, 0);
        Interlocked.Exchange(ref _jwtRevokedRejectedCount, 0);
        Interlocked.Exchange(ref _passwordHashBulkheadRejectedCount, 0);
        Interlocked.Exchange(ref _batcherFlushedItemsCount, 0);
        Interlocked.Exchange(ref _inFlightRequestsCount, 0);
        Interlocked.Exchange(ref _ipPenaltyBoxRejectedCount, 0);
        Interlocked.Exchange(ref _ipBansIssuedCount, 0);
        Interlocked.Exchange(ref _decompressionBombRejectedCount, 0);
        Interlocked.Exchange(ref _tenantCatalogShieldRejectedCount, 0);
        Interlocked.Exchange(ref _spoofedIpIgnoredCount, 0);
        Interlocked.Exchange(ref _rateLimitRejectedCount, 0);
    }
}
