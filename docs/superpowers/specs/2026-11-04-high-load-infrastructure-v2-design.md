# Design Specification: High-Load Infrastructure Suite v2 (Extreme Concurrency & Over-Engineered Edition)

**Date:** 2026-11-04  
**Status:** Draft for Review  
**Scope:** Architectural Subsystem (`Common.*` & `ExamPrism.Infrastructure.*`)  

---

## 1. Context and Goals

Building upon the Phase 1 High-Load Multi-Container Infrastructure Suite (`2026-11-04-high-load-infrastructure-design.md`), ExamPrism requires a second-generation, ultra-resilient infrastructure layer capable of surviving extreme examination traffic patterns:
* **Thundering Herd Login Storms:** 10,000+ candidates authenticating within seconds at exam start time without CPU-starving active exam threads or Orleans silo heartbeats via PBKDF2 password hashing.
* **Massive Concurrent Exam Autosaves:** Tens of thousands of high-frequency write operations coalesced via asynchronous in-memory micro-batching (`System.Threading.Channels`) before hitting PostgreSQL.
* **Multi-Tenant Connection Pool & Prepared Statement Scaling:** Bounded `NpgsqlDataSource` pooling per tenant with LRU/idle eviction, automatic server-side prepared statements (`MaxAutoPrepare`), and dedicated Read-Replica `DbContext` routing (`IReadOnlyCatalogDbContext`, `IReadOnlyAppDbContext`).
* **Self-Regulating Adaptive Load Shedding:** AIMD / Little's Law latency-gradient concurrency control (`AdaptiveLoadSheddingMiddleware`) that sheds low-priority requests (`503 Service Unavailable`) when EWMA latency or ThreadPool queue depth spikes, preserving capacity for `RequestPriority.Critical` exam submissions.
* **Anti-DDoS Single-Flight Tenant Resolution & Distributed OutputCache:** Coalesced `HybridCache.GetOrCreateAsync` lookups with negative caching and jittered TTL in `CatalogMultiTenantStore`, plus a Redis-backed `IOutputCacheStore` (`RedisOutputCacheStore`) supporting cross-container tag eviction.
* **Zero-DB Distributed JWT Revocation & Adaptive Telemetry:** Instant cluster-wide JWT `jti` and user-level token revocation backed by `HybridCacheService` + `ICacheBackplane`, paired with `AdaptiveTraceSampler` and extended Prometheus metrics (`/metrics`).

All components strictly adhere to the **Adaptive Dual-Mode** design: when Redis or Read-Replicas are configured, distributed cluster-wide implementations engage; when absent in local development or automated tests, deterministic in-memory/primary fallbacks operate transparently.

---

## 2. Architecture & Layering Rules

To preserve 100% compliance with `ArchitectureTests`:
1. **`Common.*` (`apps/backend/src/Common`)**:
   * Contains only public abstractions, enums, records, endpoint metadata extensions, and diagnostics (`IReadOnlyCatalogDbContext` / `IReadOnlyAppDbContext` if not referencing Infrastructure types, or `ITenantDataSourceRegistry`, `RequestPriority`, `PriorityEndpointExtensions`, `IWriteBehindBatcher<T>`, `IBatchFlushHandler<T>`, `ITokenRevocationService`, `ExamPrismDiagnostics`).
   * **Note on Entity Types:** Because `Tenant`, `PlatformUser`, and `ApplicationUser` reside in `ExamPrism.Infrastructure.*`, `Common.*` must **never** reference `ExamPrism.Infrastructure.*`. Therefore:
     * Read-only `DbContext` wrappers (`CatalogReadDbContext`, `AppReadDbContext`) and `ITenantDataSourceRegistry` reside in `ExamPrism.Infrastructure.Persistence` (where `CatalogDbContext` and `AppDbContext` already live and are injected by handlers).
     * Pure infrastructure-agnostic contracts (`RequestPriority`, `PriorityEndpointExtensions`, `IWriteBehindBatcher<TItem>`, `IBatchFlushHandler<TItem>`, `ITokenRevocationService`, `ExamPrismDiagnostics`) reside in `Common.*`.
2. **`ExamPrism.Infrastructure.*` (`apps/backend/src/Infrastructure`)**:
   * Houses all concrete implementations, middleware, background services, Redis scripts, EF Core contexts, and DI setup classes.
3. **`ExamPrism.Features.*` & `ExamPrism.Endpoints.*`**:
   * Feature handlers remain `public sealed`, end with `Handler`, use non-defaulted constructors, and never reference ASP.NET Core HTTP types or Infrastructure interfaces.
   * Endpoints remain `public sealed`, implement `IEndpoint`, execute inline inside `MapEndpoint`, and never reference `DbContext`.

---

## 3. Configuration Schema (`appsettings.json` Additions)

```json
{
  "RuntimeTuning": {
    "MinWorkerThreads": 128,
    "MinCompletionPortThreads": 128,
    "MaxConcurrentConnections": 10000,
    "MaxRequestBodySizeBytes": 10485760,
    "Http2MaxStreamsPerConnection": 250,
    "KeepAliveTimeoutSeconds": 120
  },
  "LoadShedding": {
    "Enabled": true,
    "MinConcurrencyLimit": 32,
    "InitialConcurrencyLimit": 256,
    "MaxConcurrencyLimit": 2048,
    "BaselineLatencyFloorMs": 5.0,
    "LatencyGradientThreshold": 0.5,
    "MaxPendingThreadPoolWorkItems": 1000,
    "LowPriorityShedFraction": 0.75,
    "NormalPriorityShedFraction": 0.90,
    "HighPriorityShedFraction": 1.00,
    "CriticalReserveMultiplier": 1.25,
    "RetryAfterSeconds": 2
  },
  "Batching": {
    "MaxChannelCapacity": 10000,
    "MaxBatchSize": 100,
    "FlushIntervalMs": 200
  },
  "MultiTenancy": {
    "DatabasePool": {
      "MaxPoolSize": 15,
      "MinPoolSize": 0,
      "ConnectionIdleLifetimeSeconds": 30,
      "ConnectionPruningIntervalSeconds": 10,
      "NoResetOnClose": true,
      "KeepAliveSeconds": 30,
      "MaxAutoPrepare": 32,
      "AutoPrepareMinUsages": 2,
      "MaxCachedTenantDataSources": 500,
      "DataSourceIdleEvictionMinutes": 30
    }
  },
  "IdentityHighLoad": {
    "MaxConcurrentPasswordHashes": 0,
    "PasswordHashQueueTimeoutMs": 5000,
    "EnableTokenRevocationCheck": true,
    "DefaultRevocationTtlMinutes": 60
  },
  "Telemetry": {
    "ServiceName": "ExamPrism.Backend",
    "EnablePrometheusEndpoint": true,
    "OtlpEndpoint": "",
    "TraceSamplingRatio": 1.0,
    "AlwaysSampleSlowRequestsMs": 500
  }
}
```

---

## 4. Detailed Subsystem Designs

### 4.1. Subsystem 1: Persistence — Read-Replica Contexts, Bounded `TenantDataSourceRegistry` & Wolverine High-Throughput Queues

#### 4.1.1. Auto-Prepared Statements in `TenantConnectionStringNormalizer`
* **`TenantDatabasePoolOptions` (`ExamPrism.Infrastructure.MultiTenancy`):**
  * Adds `int MaxAutoPrepare { get; set; } = 32`, `int AutoPrepareMinUsages { get; set; } = 2`, `int MaxCachedTenantDataSources { get; set; } = 500`, `int DataSourceIdleEvictionMinutes { get; set; } = 30`.
* **`TenantConnectionStringNormalizer.Normalize`:**
  * Sets `builder.MaxAutoPrepare = pool.MaxAutoPrepare` and `builder.AutoPrepareMinUsages = Math.Max(1, pool.AutoPrepareMinUsages)` when `pool.MaxAutoPrepare > 0`.

#### 4.1.2. Bounded `TenantDataSourceRegistry` (`ExamPrism.Infrastructure.Persistence`)
* **Problem Solved:** Calling `options.UseNpgsql(connectionString)` per request forces EF Core to hash connection strings or manage unbounded connection pools across thousands of tenants.
* **Implementation (`TenantDataSourceRegistry : IHostedService, IAsyncDisposable`):**
  * Maintains a thread-safe `ConcurrentDictionary<string, CachedDataSourceEntry>` keyed by normalized connection string.
  * Each `CachedDataSourceEntry` wraps an `NpgsqlDataSource` (built via `NpgsqlDataSource.Create(normalizedConnectionString)`) and tracks `long LastAccessedUnixMs`.
  * `GetOrCreateDataSource(string normalizedConnectionString)`:
    * Returns existing `NpgsqlDataSource` and updates `LastAccessedUnixMs` atomically.
    * When creating a new entry, if `Count >= MaxCachedTenantDataSources`, evicts and asynchronously disposes the least-recently-used (`LRU`) entry outside the active path, and updates `ExamPrismDiagnostics.SetActiveTenantDataSources(Count)`.
  * Background timer (`PeriodicTimer` every 5 minutes) sweeps and disposes entries idle longer than `DataSourceIdleEvictionMinutes`.

#### 4.1.3. Read-Only Replica `DbContext` Types (`ExamPrism.Infrastructure.Persistence`)
* **`CatalogReadDbContext` & `AppReadDbContext`:**
  * Sealed subclasses or read-configured wrappers:
    * `CatalogReadDbContext(DbContextOptions<CatalogReadDbContext> options)` (or `CatalogReadDbContext` sharing the `CatalogDbContext` model with `QueryTrackingBehavior.NoTracking`).
    * To ensure EF Core migrations and model caching remain 100% compatible without duplicate `IdentityDbContext` model conflicts:
      * Provide `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` as lightweight facades (`public sealed class ReadOnlyCatalogDbContext` / `public sealed class ReadOnlyAppDbContext`) that hold a dedicated `CatalogDbContext` / `AppDbContext` instance constructed from the **Read-Replica `NpgsqlDataSource`** with `ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking` and `ChangeTracker.AutoDetectChangesEnabled = false`.
      * Expose `IQueryable<T>` properties (`Tenants`, `Users`, `PlatformRefreshTokens` on `ReadOnlyCatalogDbContext`; `Users`, `RefreshTokens` on `ReadOnlyAppDbContext`) and `Set<T>() => _inner.Set<T>().AsNoTracking()`.
      * If `PersistenceOptions.CatalogReadConnection` is empty, `ReadOnlyCatalogDbContext` uses the primary `CatalogConnection` data source.
      * If `PersistenceOptions.TenantReadReplicaHost` is empty, `ReadOnlyAppDbContext` uses the tenant's primary normalized connection string via `TenantDataSourceRegistry`.

#### 4.1.4. Wolverine Local Queue Bulkheads & Durability (`MultiTenancySetup.UseWolverine`)
* Configures Wolverine local queues with bounded parallelism and error recovery:
  * `opts.LocalQueue("critical").MaximumParallelMessages(Environment.ProcessorCount * 4);`
  * `opts.LocalQueue("background").MaximumParallelMessages(Math.Max(2, Environment.ProcessorCount));`
  * Retains transient `NpgsqlException` and `TimeoutException` cooldown retries and moves exhausted messages to the error queue with structured logging.

---

### 4.2. Subsystem 2: Adaptive Load Shedding (Little's Law / AIMD) & Runtime Tuning

#### 4.2.1. Request Priority Metadata (`Common.Http`)
* **`RequestPriority` (`Common/Http/RequestPriority.cs`):**
  ```csharp
  namespace Common.Http;

  public enum RequestPriority
  {
      Low = 0,
      Normal = 1,
      High = 2,
      Critical = 3
  }

  public sealed record RequestPriorityMetadata(RequestPriority Priority);
  ```
* **`PriorityEndpointExtensions` (`Common/Http/PriorityEndpointExtensions.cs`):**
  * `WithRequestPriority<TBuilder>(this TBuilder builder, RequestPriority priority)` attaches `new RequestPriorityMetadata(priority)`.

#### 4.2.2. `AdaptiveConcurrencyLimiter` & `AdaptiveLoadSheddingMiddleware` (`ExamPrism.Infrastructure.Http`)
* **Algorithm (Gradient2 / AIMD):**
  * Tracks `_inFlight` (`Interlocked`), `_currentLimit` (`double`, initialized to `InitialConcurrencyLimit`), `_ewmaLatencyMs` (exponentially weighted moving average with $\alpha = 0.2$), and `_minLatencyMs` (rolling minimum bounded below by `BaselineLatencyFloorMs`).
  * On each completed request (duration $R$ ms):
    * Updates `_ewmaLatencyMs`.
    * Computes $\text{gradient} = \text{clamp}\left(\frac{\text{max}(\text{BaselineLatencyFloorMs}, \min)}{\text{max}(\text{BaselineLatencyFloorMs}, \text{ewma})}, 0.5, 1.0\right)$.
    * Checks ThreadPool pressure: `ThreadPool.PendingWorkItemCount > MaxPendingThreadPoolWorkItems`.
    * If $\text{gradient} < \text{LatencyGradientThreshold}$ or ThreadPool is overloaded:
      * Multiplicative decrease: $\text{newLimit} = \max(\text{MinConcurrencyLimit}, \text{currentLimit} \times \text{gradient})$.
    * Else if `_inFlight >= _currentLimit * 0.5`:
      * Additive increase: $\text{newLimit} = \min(\text{MaxConcurrencyLimit}, \text{currentLimit} + 1)$.
  * **Priority-Tiered Admission Control (`TryAcquire(RequestPriority priority, out IDisposable? lease)`):**
    * `Low`: admitted if `_inFlight < _currentLimit * LowPriorityShedFraction` (`0.75`).
    * `Normal`: admitted if `_inFlight < _currentLimit * NormalPriorityShedFraction` (`0.90`).
    * `High`: admitted if `_inFlight < _currentLimit * HighPriorityShedFraction` (`1.00`).
    * `Critical`: admitted if `_inFlight < _currentLimit * CriticalReserveMultiplier` (`1.25`).
    * Infrastructure paths (`/health*`, `/metrics`, `/openapi*`, `/scalar*`) always bypass shedding.
  * **Rejection Response:**
    * When rejected, increments `ExamPrismDiagnostics.RecordLoadSheddingRejection(priority.ToString())`, sets headers `Retry-After: {RetryAfterSeconds}` and `X-Load-Shed: true`, and returns `503 Service Unavailable` with RFC 7807 `ProblemDetails`.

#### 4.2.3. Kestrel & ThreadPool Pre-Warming (`RuntimeTuningSetup` in `ExamPrism.Infrastructure.Http`)
* On startup (`AddAppRuntimeTuning` / `ConfigureKestrel`):
  * Calls `ThreadPool.GetMinThreads(out var curWorker, out var curIo)` and raises minimums if below `RuntimeTuningOptions.MinWorkerThreads` / `MinCompletionPortThreads`.
  * Configures Kestrel `Limits.MaxConcurrentConnections`, `Limits.MaxRequestBodySize`, `Limits.Http2.MaxStreamsPerConnection`, and `Limits.KeepAliveTimeout`.

---

### 4.3. Subsystem 3: Write-Behind Micro-Batching Engine (`Common.Batching` & `ExamPrism.Infrastructure.Batching`)

#### 4.3.1. Abstractions (`Common/Batching`)
* **`IWriteBehindBatcher<TItem>`:**
  ```csharp
  namespace Common.Batching;

  public interface IWriteBehindBatcher<in TItem>
  {
      ValueTask EnqueueAsync(TItem item, CancellationToken cancellationToken = default);
      int PendingCount { get; }
  }
  ```
* **`IBatchFlushHandler<TItem>`:**
  ```csharp
  namespace Common.Batching;

  public interface IBatchFlushHandler<in TItem>
  {
      Task FlushBatchAsync(IReadOnlyList<TItem> items, CancellationToken cancellationToken = default);
  }
  ```

#### 4.3.2. Implementation (`ChannelWriteBehindBatcher<TItem>` in `ExamPrism.Infrastructure.Batching`)
* Uses `Channel.CreateBounded<TItem>(new BoundedChannelOptions(options.MaxChannelCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false })`.
* Implements `BackgroundService` and `IWriteBehindBatcher<TItem>`:
  * Background loop waits on `Reader.WaitToReadAsync(stoppingToken)`.
  * Once an item arrives, collects up to `MaxBatchSize` items within a `FlushIntervalMs` window (using `CancellationTokenSource` timeout for the window if `Reader.TryRead` drains current available items before reaching `MaxBatchSize`).
  * Creates an `AsyncServiceScope`, resolves `IBatchFlushHandler<TItem>`, and invokes `FlushBatchAsync(batch, ct)`.
  * Increments `ExamPrismDiagnostics.RecordBatchFlushed(batch.Count)`.
  * On `StopAsync` (graceful shutdown), marks the channel writer complete and drains all remaining buffered items in batches before returning.
* **DI Registration Helper (`BatchingSetup`):**
  * `services.AddAppBatching(configuration)` registers `BatchingOptions` and provides `services.AddWriteBehindBatcher<TItem, THandler>()` so any feature domain (e.g., exam answer autosaves, audit logs, telemetry events) can plug in a micro-batcher with one line.

---

### 4.4. Subsystem 4: Anti-DDoS Single-Flight Tenant Cache & Distributed Redis OutputCache

#### 4.4.1. Single-Flight Stampede Protection in `CatalogMultiTenantStore`
* **Current Flaw:** `CatalogMultiTenantStore.GetByIdentifierAsync` and `GetAsync` first check cache with `DisableUnderlyingData` and then run a raw EF query `_db.Tenants.AsNoTracking().FirstOrDefaultAsync(...)` outside `HybridCache.GetOrCreateAsync`. Under a 2,000-request burst for a cold tenant or a random non-existent subdomain attack, all 2,000 requests miss L1/L2 simultaneously and execute 2,000 duplicate SQL queries against `CatalogDbContext`.
* **Fix:**
  * Use `ConcurrentDictionary<string, SemaphoreSlim>` single-flight locks per lookup key (or unified `HybridCache.GetOrCreateAsync` factory with adaptive TTL) inside `CatalogMultiTenantStore` so concurrent misses on the same slug/id coalesce into **exactly 1 database query**.
  * Apply deterministic/bounded TTL jitter ($\pm 10\%$) on positive cache entries to prevent synchronized mass expiration across tenants.
  * When `ReadOnlyCatalogDbContext` is available, read tenant catalog metadata from the read-only context.

#### 4.4.2. Distributed `RedisOutputCacheStore` (`ExamPrism.Infrastructure.Http`)
* **`RedisOutputCacheStore : IOutputCacheStore`:**
  * Wraps `IConnectionMultiplexer` (`StackExchange.Redis`) and an inner fallback `IOutputCacheStore` (in-memory).
  * `GetAsync(string key, CancellationToken token)`: reads `byte[]` from Redis key `$"{prefix}outputcache:val:{key}"`. On Redis exception, falls back to inner in-memory store.
  * `SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken token)`:
    * Stores `value` in `$"{prefix}outputcache:val:{key}"` with `validFor` expiry.
    * For each `tag` in `tags`, adds `key` to Redis Set `$"{prefix}outputcache:tag:{tag}"` and sets expiry on the tag set.
  * `EvictByTagAsync(string tag, CancellationToken token)`:
    * Executes an atomic Lua script that reads all members of `$"{prefix}outputcache:tag:{tag}"`, deletes each key, and deletes the tag set.
    * Also evicts from the local fallback in-memory store.
* **Wiring in `HttpPerformanceSetup.AddAppHttpPerformance`:**
  * Registers `RedisOutputCacheStore` as `IOutputCacheStore` when `ConnectionStrings:Redis` is configured, seamlessly upgrading `UseOutputCache()` from single-container memory to cluster-wide Redis storage.

---

### 4.5. Subsystem 5: Identity High-Load Hardening — Password-Hashing CPU Bulkhead & Distributed JWT Revocation

#### 4.5.1. `BulkheadPasswordHasher<TUser>` (`ExamPrism.Infrastructure.Identity`)
* **Problem Solved:** `PasswordHasher<TUser>` runs CPU-intensive PBKDF2 iterations. During a 10,000-user exam start login spike, unbounded concurrent password verifications saturate 100% of CPU cores and starve Kestrel, Wolverine, and Orleans heartbeat threads.
* **Implementation (`BulkheadPasswordHasher<TUser> : IPasswordHasher<TUser>`):**
  * Wraps the default `PasswordHasher<TUser>` with a process-wide `SemaphoreSlim` sized to `IdentityHighLoadOptions.MaxConcurrentPasswordHashes` (if `<= 0`, defaults to `Math.Max(2, Environment.ProcessorCount)`).
  * Before `HashPassword` or `VerifyHashedPassword`, calls `_semaphore.Wait(timeoutMs)`.
  * If the semaphore is not acquired within `PasswordHashQueueTimeoutMs` (default `5000ms`), increments `ExamPrismDiagnostics.RecordPasswordHashBulkheadRejection()` and throws `PasswordHashBulkheadRejectedException` (mapped in `GlobalExceptionHandler` to `503 Service Unavailable` / `429 Too Many Requests` with `Retry-After: 2`).

#### 4.5.2. Zero-DB Distributed JWT Revocation (`Common.Security` & `ExamPrism.Infrastructure.Identity`)
* **Abstraction (`Common/Security/ITokenRevocationService.cs`):**
  ```csharp
  namespace Common.Security;

  public interface ITokenRevocationService
  {
      ValueTask RevokeTokenAsync(string jti, TimeSpan? ttl = null, CancellationToken cancellationToken = default);
      ValueTask RevokeUserTokensAsync(Guid userId, string? tenantId = null, TimeSpan? ttl = null, CancellationToken cancellationToken = default);
      ValueTask<bool> IsTokenRevokedAsync(string? jti, Guid? userId = null, string? tenantId = null, DateTimeOffset? issuedAtUtc = null, CancellationToken cancellationToken = default);
  }
  ```
* **Implementation (`HybridCacheTokenRevocationService : ITokenRevocationService`):**
  * Uses `ICacheService` (`HybridCacheService`, which automatically syncs L1 evictions across containers via `ICacheBackplane`!).
  * `RevokeTokenAsync(jti, ttl)`: writes sentinel `"1"` to key `auth:revoked:jti:{jti}` with TTL `ttl ?? DefaultRevocationTtlMinutes` and publishes backplane invalidation so any cached non-revoked state is cleared.
  * `RevokeUserTokensAsync(userId, tenantId, ttl)`: writes current Unix timestamp (seconds) to `auth:revoked:user:{tenantScope}:{userId:D}` and publishes backplane invalidation.
  * `IsTokenRevokedAsync(jti, userId, tenantId, issuedAtUtc)`:
    * Fast L1 lookup (0 network/DB hops on cache hit).
    * Checks `jti` key and user revocation timestamp against token `iat` (`issuedAtUtc`).
* **Integration in `JwtTenantValidator.ValidateAsync`:**
  * After tenant/scope validation succeeds, if `IdentityHighLoadOptions.EnableTokenRevocationCheck` is `true` and `ITokenRevocationService` is registered:
    * Extracts `jti`, `sub`/`NameIdentifier`, and `iat` claims from `context.Principal`.
    * Calls `await revocationService.IsTokenRevokedAsync(...)`.
    * If revoked, increments `ExamPrismDiagnostics.RecordJwtRevokedRejection()`, sets `context.HttpContext.Items[AuthFailureItemKey] = TokenRevokedErrorCode`, and calls `context.Fail("Token has been revoked.")`.

---

### 4.6. Subsystem 6: High-Volume Telemetry — `AdaptiveTraceSampler` & Extended Metrics

#### 4.6.1. `AdaptiveTraceSampler : Sampler` (`ExamPrism.Infrastructure.Telemetry`)
* Inherits from `OpenTelemetry.Trace.Sampler`:
  * Always samples (`SamplingDecision.RecordAndSample`) if:
    * Parent activity is already sampled (`samplingParameters.ParentContext.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)`), OR
    * Activity name or tags indicate high-priority/error/slow operations (`exam.critical`, `error`, `slow_query`), OR
    * `TraceSamplingRatio >= 1.0`.
  * Otherwise uses deterministic trace-id probability (`TraceIdRatioBasedSampler(TraceSamplingRatio)`).

#### 4.6.2. Extended `ExamPrismDiagnostics` & `/metrics` Scraping
* New thread-safe counters and gauges in `ExamPrismDiagnostics`:
  * `examprism.http.load_shedding_rejected` (`Counter<long>`, exposed as `examprism_load_shedding_rejected_total`)
  * `examprism.auth.jwt_revoked_rejected` (`Counter<long>`, exposed as `examprism_auth_jwt_revoked_rejected_total`)
  * `examprism.auth.password_hash_bulkhead_rejected` (`Counter<long>`, exposed as `examprism_auth_password_hash_bulkhead_rejected_total`)
  * `examprism.batcher.flushed_items` (`Counter<long>`, exposed as `examprism_batcher_flushed_items_total`)
  * `examprism.db.tenant_datasources_active` (gauge snapshot `ActiveTenantDataSourcesCount`, exposed as `examprism_db_tenant_datasources_active`)
  * `examprism.http.in_flight_requests` (gauge snapshot `InFlightRequestsCount`, exposed as `examprism_http_in_flight_requests`)

---

## 5. Updated Middleware Pipeline (`Program.cs`)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddInfrastructure();

var app = builder.Build();

app.UseAppReverseProxy();                         // 1. Forwarded headers & real client IP
app.UseAppLocalization();                         // 2. Request culture
app.UseExceptionHandler();                        // 3. Global ProblemDetails exception boundary
app.UseRequestDecompression();                    // 4. Decompress incoming gzip/brotli bodies
app.UseResponseCompression();                     // 5. Compress outgoing responses
app.UseRouting();                                 // 6. Endpoint matching (resolves RequestPriorityMetadata)
app.UseAdaptiveLoadShedding();                    // 7. AIMD / Priority load shedding using matched endpoint priority
app.UseRequestTimeouts();                         // 8. Per-endpoint & default timeouts
app.UseSerilogRequestLogging(...);                // 9. Structured HTTP logging
app.UseAppCors();                                 // 10. CORS
app.UseMultiTenant();                             // 11. Single-flight cached Finbuckle tenant resolution
app.UseMiddleware<TenantAccessGuardMiddleware>(); // 12. Active tenant guard
app.UseAuthentication();                          // 13. JWT auth + Distributed Token Revocation check
app.UseLogEnrichment();                           // 14. Enrich TenantId, UserId, CorrelationId
app.UseRateLimiter();                             // 15. Global + SlidingWindow + Bulkhead rate limiters
app.UseOutputCache();                             // 16. Distributed Redis / Tenant-scoped output cache
app.UseAuthorization();                           // 17. RBAC policies
app.UseAppTelemetry();                            // 18. /metrics Prometheus endpoint

if (app.Environment.IsDevelopment())
{
    app.MapAppOpenApi();
}

app.MapEndpoints();
```

---

## 6. Verification and Testing Strategy

1. **`ArchitectureTests`:**
   * All new types in `Common.*` and `ExamPrism.Infrastructure.*` must pass every test in `ArchitectureTests.cs` with zero violations.
2. **Unit & Integration Tests (`apps/backend/tests/ExamPrism.Tests/Infrastructure`):**
   * `TenantDataSourceRegistryTests` & `ReadOnlyDbContextTests`: Verify `NpgsqlDataSource` reuse, LRU eviction when `MaxCachedTenantDataSources` is exceeded, `MaxAutoPrepare` normalization, and `ReadOnlyCatalogDbContext` / `ReadOnlyAppDbContext` `NoTracking` behavior.
   * `AdaptiveLoadSheddingTests`: Verify AIMD limit reduction under simulated high latency / overload, priority-based rejection (`Low` shed before `Normal` before `Critical`), `503` ProblemDetails response with `Retry-After` and `X-Load-Shed: true`, and `/health` exemption.
   * `ChannelWriteBehindBatcherTests`: Verify batching by `MaxBatchSize`, batching by `FlushIntervalMs` timeout, and complete drain on `StopAsync`.
   * `SingleFlightTenantStoreTests` & `RedisOutputCacheStoreTests`: Verify 50 concurrent cold requests for the same tenant slug trigger only 1 `CatalogDbContext` query, and verify `RedisOutputCacheStore` get/set/tag-eviction + fail-open fallback.
   * `IdentityHighLoadTests`: Verify `BulkheadPasswordHasher` concurrency bounding and timeout rejection, and verify `HybridCacheTokenRevocationService` + `JwtTenantValidator` instant token (`jti`) and user-wide revocation.
   * `AdaptiveTelemetryTests`: Verify `AdaptiveTraceSampler` sampling decisions and new Prometheus metrics on `GET /metrics`.
