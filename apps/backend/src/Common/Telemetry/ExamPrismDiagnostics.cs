namespace Common.Telemetry;

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

    private static long _slowQueriesCount;
    private static long _cacheBackplaneEvictionsCount;
    private static long _idempotencyReplaysCount;
    private static long _idempotencyConflictsCount;

    public static long SlowQueriesCount => Interlocked.Read(ref _slowQueriesCount);
    public static long CacheBackplaneEvictionsCount => Interlocked.Read(ref _cacheBackplaneEvictionsCount);
    public static long IdempotencyReplaysCount => Interlocked.Read(ref _idempotencyReplaysCount);
    public static long IdempotencyConflictsCount => Interlocked.Read(ref _idempotencyConflictsCount);

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

    public static void ResetForTesting()
    {
        Interlocked.Exchange(ref _slowQueriesCount, 0);
        Interlocked.Exchange(ref _cacheBackplaneEvictionsCount, 0);
        Interlocked.Exchange(ref _idempotencyReplaysCount, 0);
        Interlocked.Exchange(ref _idempotencyConflictsCount, 0);
    }
}
