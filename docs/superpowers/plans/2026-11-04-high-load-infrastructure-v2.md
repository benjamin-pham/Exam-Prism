# High-Load Infrastructure Suite v2 (Extreme Concurrency & Over-Engineered Edition) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade `ExamPrism.Infrastructure` and `Common` with bounded multi-tenant `NpgsqlDataSource` pooling + `MaxAutoPrepare`, Read-Replica `DbContext` facades (`ReadOnlyCatalogDbContext`, `ReadOnlyAppDbContext`), AIMD/Little's Law priority-tiered `AdaptiveLoadSheddingMiddleware`, Kestrel/ThreadPool runtime pre-warming, `System.Threading.Channels` write-behind micro-batching (`ChannelWriteBehindBatcher<T>`), single-flight stampede protection in `CatalogMultiTenantStore`, distributed `RedisOutputCacheStore`, CPU-bulkhead password hashing (`BulkheadPasswordHasher<TUser>`), zero-DB distributed JWT revocation (`HybridCacheTokenRevocationService`), and `AdaptiveTraceSampler` + extended Prometheus metrics.

**Architecture:** Pure domain-agnostic contracts (`RequestPriority`, `PriorityEndpointExtensions`, `IWriteBehindBatcher<T>`, `IBatchFlushHandler<T>`, `ITokenRevocationService`, `ExamPrismDiagnostics`) live in `Common.*` with zero references to `ExamPrism.Infrastructure.*`. Concrete high-load engines in `ExamPrism.Infrastructure.*` automatically engage Redis/Replica features when configured and fall back to deterministic in-memory/primary defaults in development and automated tests.

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core 10 Minimal APIs, `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `StackExchange.Redis` 2.11.0, `Microsoft.Extensions.Caching.Hybrid` 10.0.0, `WolverineFx` 4.8.0, `OpenTelemetry` 1.18.0, `System.Threading.Channels`, xUnit, FluentAssertions, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-04-high-load-infrastructure-v2-design.md`

## Global Constraints

- Target framework `.NET 10` (`net10.0`); do not add any external NuGet packages outside `apps/backend/Directory.Packages.props`.
- Preserve 100% of `ArchitectureTests`:
  - `Common.*` must never reference `ExamPrism.Infrastructure`, `ExamPrism.Features`, or `ExamPrism.Endpoints`.
  - `ExamPrism.Features.*` must never reference `ExamPrism.Infrastructure.*` interfaces (note: concrete classes like `ReadOnlyCatalogDbContext`, `ReadOnlyAppDbContext`, `CatalogDbContext`, `AppDbContext` in `ExamPrism.Infrastructure.Persistence` are allowed, whereas `interface` types in `ExamPrism.Infrastructure.*` must not be injected into `ExamPrism.Features.*`).
  - `ExamPrism.Endpoints.*` classes must be `public sealed`, implement `IEndpoint`, have no `Handle`/`HandleAsync` methods, and never reference `DbContext`.
- Dual-Mode operation: when `ConnectionStrings:Redis` or `Persistence:CatalogReadConnection` / `Persistence:TenantReadReplicaHost` are empty, all features fall back to in-memory or primary database connections without throwing.
- All Redis operations in `RedisOutputCacheStore` must catch `RedisException` and `TimeoutException` and fail open to the inner in-memory `IOutputCacheStore` with a `LogWarning`.

## Review Focus

1. **Unbounded `NpgsqlDataSource` memory leak across thousands of tenants or disposing an active data source in-flight**: `TenantDataSourceRegistry` must bound its cache to `MaxCachedTenantDataSources` using LRU tracking (`LastAccessedUnixMs`) and updates `ExamPrismDiagnostics.ActiveTenantDataSourcesCount` whenever entries are added or evicted (tested in Task 1).
2. **Adaptive load shedding killing `/health/live`, `/health/ready`, `/metrics`, or `RequestPriority.Critical` exam submissions during normal traffic spikes**: `AdaptiveLoadSheddingMiddleware` must exempt `/health*`, `/metrics`, `/openapi*`, `/scalar*`, must shed `Low` (75%) and `Normal` (90%) before `High` (100%), and must grant `Critical` requests up to `CriticalReserveMultiplier` (`1.25x`) of the current AIMD concurrency limit (tested in Task 2).
3. **Data loss of buffered write-behind items when container receives `SIGTERM` shutdown signal**: `ChannelWriteBehindBatcher<TItem>.StopAsync` must complete the `ChannelWriter<TItem>` and drain/flush all remaining items in `ChannelReader<TItem>` before returning (tested in Task 3).
4. **Thundering-herd cache stampede on `CatalogMultiTenantStore` when 100 concurrent requests hit an uncached or non-existent tenant slug**: `CatalogMultiTenantStore.GetByIdentifierAsync` and `GetAsync` must coalesce concurrent cold lookups per key using a single-flight `SemaphoreSlim` + double-check cache read so 50 concurrent cold requests execute only 1 query against `CatalogDbContext` (tested in Task 4).
5. **CPU saturation during login storms or stale L1 cache permitting revoked JWTs**: `BulkheadPasswordHasher<TUser>` must cap concurrent PBKDF2 operations via `SemaphoreSlim` and throw `PasswordHashBulkheadRejectedException` (mapped to `503 Service Unavailable` with `Retry-After: 2`) when queue wait exceeds `PasswordHashQueueTimeoutMs`; `HybridCacheTokenRevocationService` must invalidate L1 + backplane immediately on `RevokeTokenAsync` / `RevokeUserTokensAsync` so `JwtTenantValidator` rejects revoked tokens with `401 Unauthorized` (tested in Task 5).

---

### Task 1: Bounded `TenantDataSourceRegistry`, `MaxAutoPrepare`, `ReadOnlyCatalogDbContext` & `ReadOnlyAppDbContext`, and Wolverine Queue Bulkheads

**Files:**
- Modify: `apps/backend/src/Common/Telemetry/ExamPrismDiagnostics.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/MultiTenancyOptions.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/TenantConnectionStringNormalizer.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/MultiTenancySetup.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/TenantDataSourceRegistry.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/ReadOnlyCatalogDbContext.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/ReadOnlyAppDbContext.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/PersistenceSetup.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/PersistenceHighLoadTests.cs`

**Interfaces:**
- Consumes: `Common.MultiTenancy.ITenantConnectionStringNormalizer`, `ExamPrism.Infrastructure.Persistence.Catalog.CatalogDbContext`, `ExamPrism.Infrastructure.Persistence.AppDbContext`
- Produces:
  - `ExamPrism.Infrastructure.Persistence.TenantDataSourceRegistry` (`NpgsqlDataSource GetOrCreate(string normalizedConnectionString)`, `int ActiveCount`, `ValueTask EvictIdleAsync(TimeSpan maxIdle, CancellationToken ct = default)`)
  - `ExamPrism.Infrastructure.Persistence.ReadOnlyCatalogDbContext` (`IQueryable<Tenant> Tenants`, `IQueryable<PlatformUser> Users`, `IQueryable<PlatformRefreshToken> PlatformRefreshTokens`, `IQueryable<TEntity> Set<TEntity>() where TEntity : class`)
  - `ExamPrism.Infrastructure.Persistence.ReadOnlyAppDbContext` (`IQueryable<ApplicationUser> Users`, `IQueryable<RefreshToken> RefreshTokens`, `IQueryable<TEntity> Set<TEntity>() where TEntity : class`)
  - `Common.Telemetry.ExamPrismDiagnostics.SetActiveTenantDataSources(long count)` and `ActiveTenantDataSourcesCount`

- [ ] **Step 1: Write the failing test in `apps/backend/tests/ExamPrism.Tests/Infrastructure/PersistenceHighLoadTests.cs`**

Create `PersistenceHighLoadTests` (`namespace ExamPrism.Tests.Infrastructure`) testing:
- `TenantConnectionStringNormalizer_AppliesMaxAutoPrepare_AndAutoPrepareMinUsages`: verifies `Normalize` sets `Max Auto Prepare=32` and `Auto Prepare Min Usages=2` when configured in `MultiTenancyOptions.DatabasePool`.
- `TenantDataSourceRegistry_ReusesDataSourceForSameConnectionString_AndEvictsLeastRecentlyUsedWhenMaxCapacityExceeded`: configures `MaxCachedTenantDataSources = 2`, requests 3 distinct normalized connection strings using `FakeTimeProvider` advancing 1 second between calls, and asserts `ActiveCount == 2`, the first connection string's `NpgsqlDataSource` was evicted, the second and third are retained, and `ExamPrismDiagnostics.ActiveTenantDataSourcesCount == 2`.
- `ReadOnlyCatalogDbContext_And_ReadOnlyAppDbContext_EnforceNoTracking_AndFallbackToPrimaryConnectionWhenReplicaNotConfigured`: resolves `ReadOnlyCatalogDbContext` from a `ServiceProvider` configured with `AddPersistence`, verifies `QueryTrackingBehavior.NoTracking` is active on underlying `DbContext`, and verifies `Tenants` queryable is non-null.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~PersistenceHighLoadTests"`
Expected: FAIL (`TenantDataSourceRegistry`, `ReadOnlyCatalogDbContext`, `ReadOnlyAppDbContext` do not exist).

- [ ] **Step 3: Implement `TenantDataSourceRegistry`, `ReadOnlyCatalogDbContext`, `ReadOnlyAppDbContext`, `MaxAutoPrepare`, and Wolverine local queue bulkheads**

- In `ExamPrismDiagnostics.cs`, add `ActiveTenantDataSourcesCount` (`SetActiveTenantDataSources(long count)`), plus the remaining v2 counters/gauges needed by later tasks (`LoadSheddingRejectedCount` / `RecordLoadSheddingRejection(string? priority = null)`, `JwtRevokedRejectedCount` / `RecordJwtRevokedRejection()`, `PasswordHashBulkheadRejectedCount` / `RecordPasswordHashBulkheadRejection()`, `BatcherFlushedItemsCount` / `RecordBatchFlushed(int itemCount)`, `InFlightRequestsCount` / `IncrementInFlightRequests()` / `DecrementInFlightRequests()`, and reset them in `ResetForTesting()`).
- In `MultiTenancyOptions.cs` (`TenantDatabasePoolOptions`), add `MaxAutoPrepare = 32`, `AutoPrepareMinUsages = 2`, `MaxCachedTenantDataSources = 500`, `DataSourceIdleEvictionMinutes = 30`.
- In `TenantConnectionStringNormalizer.cs`, set `MaxAutoPrepare = pool.MaxAutoPrepare` and `AutoPrepareMinUsages = Math.Max(1, pool.AutoPrepareMinUsages)` when `pool.MaxAutoPrepare > 0`.
- Create `TenantDataSourceRegistry.cs` (`public sealed class TenantDataSourceRegistry : IHostedService, IAsyncDisposable`) in `apps/backend/src/Infrastructure/Persistence/`:
  - Uses `ConcurrentDictionary<string, DataSourceEntry>` where `DataSourceEntry` holds `NpgsqlDataSource DataSource` and `long LastAccessedUnixMs`.
  - `GetOrCreate(string normalizedConnectionString)` returns existing or creates via `NpgsqlDataSource.Create(normalizedConnectionString)`, evicting the entry with smallest `LastAccessedUnixMs` when `Count > MaxCachedTenantDataSources`.
- Create `ReadOnlyCatalogDbContext.cs` and `ReadOnlyAppDbContext.cs` in `apps/backend/src/Infrastructure/Persistence/` wrapping `CatalogDbContext` and `AppDbContext` configured with `QueryTrackingBehavior.NoTracking` and `AutoDetectChangesEnabled = false` over the read-replica (or fallback primary) `NpgsqlDataSource`.
- Update `PersistenceSetup.cs` to register `TenantDataSourceRegistry`, `ReadOnlyCatalogDbContext`, and `ReadOnlyAppDbContext`, and use `TenantDataSourceRegistry.GetOrCreate(...)` in `AppDbContext` registration.
- Update `MultiTenancySetup.UseWolverine` to configure `opts.LocalQueue("critical").MaximumParallelMessages(Math.Max(4, Environment.ProcessorCount * 4))` and `opts.LocalQueue("background").MaximumParallelMessages(Math.Max(2, Environment.ProcessorCount))`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~PersistenceHighLoadTests|FullyQualifiedName~ResiliencePersistenceAndTelemetryTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Telemetry/ExamPrismDiagnostics.cs apps/backend/src/Infrastructure/MultiTenancy apps/backend/src/Infrastructure/Persistence apps/backend/tests/ExamPrism.Tests/Infrastructure/PersistenceHighLoadTests.cs
git commit -m "feat(persistence): add bounded TenantDataSourceRegistry, auto-prepared statements, and read-only DbContexts"
```

---

### Task 2: Adaptive Load Shedding (`AdaptiveConcurrencyLimiter`, `AdaptiveLoadSheddingMiddleware`, `RequestPriority`) & Kestrel/ThreadPool Pre-Warming (`RuntimeTuningSetup`)

**Files:**
- Create: `apps/backend/src/Common/Http/RequestPriority.cs`
- Create: `apps/backend/src/Common/Http/PriorityEndpointExtensions.cs`
- Create: `apps/backend/src/Infrastructure/Http/LoadSheddingOptions.cs`
- Create: `apps/backend/src/Infrastructure/Http/AdaptiveConcurrencyLimiter.cs`
- Create: `apps/backend/src/Infrastructure/Http/AdaptiveLoadSheddingMiddleware.cs`
- Create: `apps/backend/src/Infrastructure/Http/RuntimeTuningOptions.cs`
- Create: `apps/backend/src/Infrastructure/Http/RuntimeTuningSetup.cs`
- Modify: `apps/backend/src/Infrastructure/Http/HttpPerformanceSetup.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/AdaptiveLoadSheddingAndRuntimeTests.cs`

**Interfaces:**
- Consumes: `Common.Telemetry.ExamPrismDiagnostics`
- Produces:
  - `Common.Http.RequestPriority` (`Low = 0`, `Normal = 1`, `High = 2`, `Critical = 3`) & `RequestPriorityMetadata(RequestPriority Priority)`
  - `Common.Http.PriorityEndpointExtensions.WithRequestPriority<TBuilder>(this TBuilder builder, RequestPriority priority)`
  - `ExamPrism.Infrastructure.Http.AdaptiveConcurrencyLimiter` (`bool TryAcquire(RequestPriority priority, out IDisposable? lease)`, `void RecordSample(double durationMs, bool isServerError = false)`, `double CurrentLimit`, `int InFlight`)
  - `ExamPrism.Infrastructure.Http.AdaptiveLoadSheddingMiddleware` & `UseAdaptiveLoadShedding(this IApplicationBuilder app)`
  - `ExamPrism.Infrastructure.Http.RuntimeTuningSetup` (`AddAppRuntimeTuning(this IServiceCollection services, IConfiguration configuration)`, `ConfigureAppKestrel(this WebApplicationBuilder builder)`)

- [ ] **Step 1: Write the failing test in `apps/backend/tests/ExamPrism.Tests/Infrastructure/AdaptiveLoadSheddingAndRuntimeTests.cs`**

Create `AdaptiveLoadSheddingAndRuntimeTests` (`namespace ExamPrism.Tests.Infrastructure`) testing:
- `AdaptiveConcurrencyLimiter_ReducesLimitUnderLatencySpike_AndShedsByPriorityTier`:
  1. Initializes `AdaptiveConcurrencyLimiter` with `InitialConcurrencyLimit = 10`, `MinConcurrencyLimit = 4`, `MaxConcurrencyLimit = 20`, `LowPriorityShedFraction = 0.7`, `NormalPriorityShedFraction = 0.9`, `HighPriorityShedFraction = 1.0`, `CriticalReserveMultiplier = 1.2`.
  2. Acquires 7 leases (`InFlight == 7`): verifies `TryAcquire(RequestPriority.Low, ...)` returns `false`, while `TryAcquire(RequestPriority.Normal, ...)` returns `true` (`InFlight == 8`).
  3. Acquires up to 10 leases (`InFlight == 10`): verifies `Normal` and `High` return `false`, while `TryAcquire(RequestPriority.Critical, ...)` still returns `true` (up to `12`).
  4. Records high latency samples (`RecordSample(500.0)` against baseline `5.0ms`) and verifies `CurrentLimit` decreases multiplicatively toward `MinConcurrencyLimit`.
- `AdaptiveLoadSheddingMiddleware_Returns503WithRetryAfterAndHeaderWhenShed_AndExemptsHealthAndMetrics`:
  1. Invokes middleware when limiter rejects `RequestPriority.Normal` request: asserts `503 ServiceUnavailable`, `Retry-After: 2`, `X-Load-Shed: true`, `application/problem+json`, and `ExamPrismDiagnostics.LoadSheddingRejectedCount == 1`.
  2. Invokes middleware for `/health/live` and `/metrics` even when limiter is full: asserts `200 OK` (bypassed).
- `RuntimeTuningSetup_AppliesThreadPoolMinimums`: verifies `RuntimeTuningSetup.ApplyThreadPoolTuning(new RuntimeTuningOptions { MinWorkerThreads = 64, MinCompletionPortThreads = 64 })` succeeds and ensures `ThreadPool.GetMinThreads` is at least `64`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~AdaptiveLoadSheddingAndRuntimeTests"`
Expected: FAIL (types do not exist).

- [ ] **Step 3: Implement `RequestPriority`, `AdaptiveConcurrencyLimiter`, `AdaptiveLoadSheddingMiddleware`, and `RuntimeTuningSetup`**

- Create `RequestPriority.cs` and `PriorityEndpointExtensions.cs` in `apps/backend/src/Common/Http/`.
- Create `LoadSheddingOptions.cs`, `AdaptiveConcurrencyLimiter.cs`, and `AdaptiveLoadSheddingMiddleware.cs` in `apps/backend/src/Infrastructure/Http/`.
- Create `RuntimeTuningOptions.cs` and `RuntimeTuningSetup.cs` in `apps/backend/src/Infrastructure/Http/`.
- Register `LoadSheddingOptions`, `AdaptiveConcurrencyLimiter`, and `AddAppRuntimeTuning` inside `HttpPerformanceSetup.AddAppHttpPerformance`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~AdaptiveLoadSheddingAndRuntimeTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Http apps/backend/src/Infrastructure/Http apps/backend/tests/ExamPrism.Tests/Infrastructure/AdaptiveLoadSheddingAndRuntimeTests.cs
git commit -m "feat(http): add AIMD adaptive load shedding middleware and Kestrel/ThreadPool runtime tuning"
```

---

### Task 3: Write-Behind Micro-Batching Engine (`IWriteBehindBatcher<T>`, `IBatchFlushHandler<T>`, `ChannelWriteBehindBatcher<T>`, `BatchingSetup`)

**Files:**
- Create: `apps/backend/src/Common/Batching/IWriteBehindBatcher.cs`
- Create: `apps/backend/src/Common/Batching/IBatchFlushHandler.cs`
- Create: `apps/backend/src/Infrastructure/Batching/BatchingOptions.cs`
- Create: `apps/backend/src/Infrastructure/Batching/ChannelWriteBehindBatcher.cs`
- Create: `apps/backend/src/Infrastructure/Batching/BatchingSetup.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/WriteBehindBatcherTests.cs`

**Interfaces:**
- Consumes: `Common.Telemetry.ExamPrismDiagnostics`, `Microsoft.Extensions.DependencyInjection.IServiceScopeFactory`
- Produces:
  - `Common.Batching.IWriteBehindBatcher<in TItem>` (`ValueTask EnqueueAsync(TItem item, CancellationToken cancellationToken = default)`, `int PendingCount { get; }`)
  - `Common.Batching.IBatchFlushHandler<TItem>` (`Task FlushBatchAsync(IReadOnlyList<TItem> items, CancellationToken cancellationToken = default)`)
  - `ExamPrism.Infrastructure.Batching.ChannelWriteBehindBatcher<TItem>`
  - `ExamPrism.Infrastructure.Batching.BatchingSetup` (`AddAppBatching`, `AddWriteBehindBatcher<TItem, THandler>`)

- [ ] **Step 1: Write the failing test in `apps/backend/tests/ExamPrism.Tests/Infrastructure/WriteBehindBatcherTests.cs`**

Create `WriteBehindBatcherTests` (`namespace ExamPrism.Tests.Infrastructure`) testing:
- `ChannelWriteBehindBatcher_FlushesImmediatelyWhenMaxBatchSizeReached_AndFlushesPartialBatchOnTimeout`: configures `MaxBatchSize = 3`, `FlushIntervalMs = 50`, enqueues 5 items, waits briefly, and asserts `IBatchFlushHandler<int>` received a batch of 3 followed by a batch of 2, and `ExamPrismDiagnostics.BatcherFlushedItemsCount == 5`.
- `ChannelWriteBehindBatcher_DrainsRemainingBufferedItemsOnGracefulStopAsync`: stops background timer/loop or enqueues items right before `StopAsync(CancellationToken.None)` and asserts all remaining queued items are flushed before `StopAsync` completes (zero data loss on shutdown).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~WriteBehindBatcherTests"`
Expected: FAIL (`IWriteBehindBatcher`, `ChannelWriteBehindBatcher` do not exist).

- [ ] **Step 3: Implement `IWriteBehindBatcher<T>`, `IBatchFlushHandler<T>`, `ChannelWriteBehindBatcher<T>`, and `BatchingSetup`**

- Create `IWriteBehindBatcher.cs` and `IBatchFlushHandler.cs` in `apps/backend/src/Common/Batching/`.
- Create `BatchingOptions.cs`, `ChannelWriteBehindBatcher.cs`, and `BatchingSetup.cs` in `apps/backend/src/Infrastructure/Batching/`.
  - `ChannelWriteBehindBatcher<TItem>` uses `Channel.CreateBounded<TItem>` with `BoundedChannelFullMode.Wait`.
  - In `ExecuteAsync`, waits via `Reader.WaitToReadAsync(stoppingToken)`, collects items up to `MaxBatchSize` within `FlushIntervalMs`, resolves `IBatchFlushHandler<TItem>` from a fresh `AsyncServiceScope`, calls `FlushBatchAsync`, and increments `ExamPrismDiagnostics.RecordBatchFlushed(batch.Count)`.
  - Overrides `StopAsync`: calls `_channel.Writer.TryComplete()`, awaits `base.StopAsync(cancellationToken)`, and drains any residual items in `_channel.Reader` in batches of `MaxBatchSize`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~WriteBehindBatcherTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Batching apps/backend/src/Infrastructure/Batching apps/backend/tests/ExamPrism.Tests/Infrastructure/WriteBehindBatcherTests.cs
git commit -m "feat(batching): add ChannelWriteBehindBatcher for high-frequency write coalescing"
```

---

### Task 4: Anti-DDoS Single-Flight Tenant Resolution (`CatalogMultiTenantStore`) & Distributed Redis OutputCache (`RedisOutputCacheStore`)

**Files:**
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs`
- Create: `apps/backend/src/Infrastructure/Http/RedisOutputCacheStore.cs`
- Modify: `apps/backend/src/Infrastructure/Http/HttpPerformanceSetup.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/AntiDdosCacheAndOutputCacheTests.cs`

**Interfaces:**
- Consumes: `StackExchange.Redis.IConnectionMultiplexer`, `Microsoft.AspNetCore.OutputCaching.IOutputCacheStore`
- Produces:
  - Single-flight stampede-protected `CatalogMultiTenantStore.GetByIdentifierAsync` & `GetAsync`
  - `ExamPrism.Infrastructure.Http.RedisOutputCacheStore : IOutputCacheStore`

- [ ] **Step 1: Write the failing test in `apps/backend/tests/ExamPrism.Tests/Infrastructure/AntiDdosCacheAndOutputCacheTests.cs`**

Create `AntiDdosCacheAndOutputCacheTests` (`namespace ExamPrism.Tests.Infrastructure`) testing:
- `CatalogMultiTenantStore_CoalescesConcurrentColdSlugLookups_IntoSingleDatabaseQuery`: attaches a query counter `DbCommandInterceptor` to an in-memory/SQLite or mocked/intercepted `CatalogDbContext`, fires 40 concurrent `Task.Run(() => store.GetByIdentifierAsync("cold-slug"))` calls simultaneously, and asserts `DbCommandInterceptor.ExecutedSelectCount == 1` (single-flight coalescing works for both found and not-found negative cached slugs).
- `RedisOutputCacheStore_StoresGetsAndEvictsByTagViaRedis_AndFailsOpenToFallbackOnRedisError`: mocks `IConnectionMultiplexer` and `IDatabase`, verifies `SetAsync` stores `byte[]` payload and adds key to tag sets (`outputcache:tag:{tag}`), verifies `GetAsync` returns stored `byte[]`, verifies `EvictByTagAsync` executes Lua script to remove tagged keys, and verifies `GetAsync`/`SetAsync` fall back to inner in-memory `IOutputCacheStore` without throwing when `IDatabase` throws `RedisConnectionException`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~AntiDdosCacheAndOutputCacheTests"`
Expected: FAIL (`RedisOutputCacheStore` does not exist and concurrent cold lookups hit DB multiple times).

- [ ] **Step 3: Implement Single-Flight Coalescing in `CatalogMultiTenantStore` and `RedisOutputCacheStore`**

- In `CatalogMultiTenantStore.cs`:
  - Add a static or instance `ConcurrentDictionary<string, SemaphoreSlim> _keyLocks = new(StringComparer.Ordinal)` (with bounded cleanup or striped 256-slot lock array `SemaphoreSlim[256]` indexed by `(uint)key.GetHashCode() % 256` so memory is O(1) and immune to DDoS key cardinality!).
  - Notice: A 256-stripe `SemaphoreSlim` array (`private static readonly SemaphoreSlim[] KeyStripes = Enumerable.Range(0, 256).Select(_ => new SemaphoreSlim(1, 1)).ToArray();`) uses fixed memory, zero allocations per random DDoS slug, and guarantees single-flight double-checked locking per key!
  - Inside `GetByIdentifierAsync` and `GetAsync`: after initial fast-path `GetCacheEntryOrDefaultAsync(key)` miss, acquire the stripe lock for `key`, re-check `GetCacheEntryOrDefaultAsync(key)`, and only if still null query `_db.Tenants.AsNoTracking()` and populate `_hybridCache` (with $\pm 10\%$ jitter on positive TTL when `CatalogCacheExpirationMinutes > 1`).
- Create `RedisOutputCacheStore.cs` in `apps/backend/src/Infrastructure/Http/` implementing `IOutputCacheStore` with Redis `StringGetAsync`, `StringSetAsync`, `SetAddAsync`, Lua tag eviction, and fallback `IOutputCacheStore`.
- Wire `RedisOutputCacheStore` into `HttpPerformanceSetup.AddAppHttpPerformance` when `IConnectionMultiplexer` is available in DI.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~AntiDdosCacheAndOutputCacheTests|FullyQualifiedName~CatalogMultiTenantStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs apps/backend/src/Infrastructure/Http/RedisOutputCacheStore.cs apps/backend/src/Infrastructure/Http/HttpPerformanceSetup.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/AntiDdosCacheAndOutputCacheTests.cs
git commit -m "feat(caching): add striped single-flight tenant cache stampede guard and RedisOutputCacheStore"
```

---

### Task 5: Identity High-Load Hardening (`BulkheadPasswordHasher`, `HybridCacheTokenRevocationService`) & Adaptive Telemetry (`AdaptiveTraceSampler`, Extended `/metrics`, Full Pipeline Wiring)

**Files:**
- Create: `apps/backend/src/Common/Security/ITokenRevocationService.cs`
- Create: `apps/backend/src/Common/Exceptions/PasswordHashBulkheadRejectedException.cs`
- Create: `apps/backend/src/Infrastructure/Identity/IdentityHighLoadOptions.cs`
- Create: `apps/backend/src/Infrastructure/Identity/BulkheadPasswordHasher.cs`
- Create: `apps/backend/src/Infrastructure/Identity/HybridCacheTokenRevocationService.cs`
- Create: `apps/backend/src/Infrastructure/Telemetry/AdaptiveTraceSampler.cs`
- Modify: `apps/backend/src/Common/Exceptions/GlobalExceptionHandler.cs`
- Modify: `apps/backend/src/Common/Endpoints/TenantProblemResults.cs`
- Modify: `apps/backend/src/Infrastructure/Identity/JwtTenantValidator.cs`
- Modify: `apps/backend/src/Infrastructure/Identity/IdentitySetup.cs`
- Modify: `apps/backend/src/Infrastructure/Telemetry/TelemetryOptions.cs`
- Modify: `apps/backend/src/Infrastructure/Telemetry/TelemetrySetup.cs`
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs`
- Modify: `apps/backend/src/Program.cs`
- Modify: `apps/backend/src/appsettings.json`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/IdentityHighLoadAndTelemetryTests.cs`

**Interfaces:**
- Consumes: `Common.Caching.ICacheService`, `Common.Telemetry.ExamPrismDiagnostics`, `Microsoft.AspNetCore.Identity.IPasswordHasher<TUser>`, `OpenTelemetry.Trace.Sampler`
- Produces:
  - `Common.Security.ITokenRevocationService` (`RevokeTokenAsync`, `RevokeUserTokensAsync`, `IsTokenRevokedAsync`)
  - `Common.Exceptions.PasswordHashBulkheadRejectedException`
  - `ExamPrism.Infrastructure.Identity.BulkheadPasswordHasher<TUser> : IPasswordHasher<TUser>`
  - `ExamPrism.Infrastructure.Identity.HybridCacheTokenRevocationService : ITokenRevocationService`
  - `ExamPrism.Infrastructure.Telemetry.AdaptiveTraceSampler : Sampler`

- [ ] **Step 1: Write the failing test in `apps/backend/tests/ExamPrism.Tests/Infrastructure/IdentityHighLoadAndTelemetryTests.cs`**

Create `IdentityHighLoadAndTelemetryTests` (`namespace ExamPrism.Tests.Infrastructure`) testing:
- `BulkheadPasswordHasher_HashesAndVerifiesNormally_AndThrowsBulkheadRejectedWhenConcurrencyExhausted`: creates `BulkheadPasswordHasher<ApplicationUser>` with `MaxConcurrentPasswordHashes = 1`, `PasswordHashQueueTimeoutMs = 50`; holds the 1 permit in a background task and asserts a second concurrent call throws `PasswordHashBulkheadRejectedException` and increments `ExamPrismDiagnostics.PasswordHashBulkheadRejectedCount`.
- `HybridCacheTokenRevocationService_And_JwtTenantValidator_RejectRevokedJti_AndRevokedUserTokens`:
  1. Calls `RevokeTokenAsync("jti-123")` and asserts `IsTokenRevokedAsync("jti-123")` is `true` while `"jti-456"` is `false`.
  2. Calls `RevokeUserTokensAsync(userId, tenantId)` at $T_1$ and asserts a token with `iat` $< T_1$ returns `IsTokenRevokedAsync == true`, while a token issued at $T_2 > T_1$ returns `false`.
  3. Invokes `JwtTenantValidator.ValidateAsync` with a principal carrying revoked `jti-123` and asserts `context.Result.Failure` is set, `AuthFailureItemKey` is `"TOKEN_REVOKED"`, and `ExamPrismDiagnostics.JwtRevokedRejectedCount == 1`.
- `AdaptiveTraceSampler_And_PrometheusMetricsEndpoint_SampleCorrectlyAndExposeAllV2Metrics`:
  1. Verifies `AdaptiveTraceSampler` with ratio `0.0` drops normal activities (`SamplingDecision.Drop`) but always samples (`SamplingDecision.RecordAndSample`) when parent is recorded or activity name/tags contain `"exam.critical"` or `"error"`.
  2. Calls `GET /metrics` via `CustomWebApplicationFactory` and asserts output contains `examprism_load_shedding_rejected_total`, `examprism_auth_jwt_revoked_rejected_total`, `examprism_auth_password_hash_bulkhead_rejected_total`, `examprism_batcher_flushed_items_total`, `examprism_db_tenant_datasources_active`, and `examprism_http_in_flight_requests`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~IdentityHighLoadAndTelemetryTests"`
Expected: FAIL (types do not exist).

- [ ] **Step 3: Implement `BulkheadPasswordHasher`, `HybridCacheTokenRevocationService`, `AdaptiveTraceSampler`, update `JwtTenantValidator`, `TelemetrySetup`, `DependencyInjection`, `Program.cs`, and `appsettings.json`**

- Create `ITokenRevocationService.cs` in `apps/backend/src/Common/Security/` and `PasswordHashBulkheadRejectedException.cs` in `apps/backend/src/Common/Exceptions/`.
- Update `GlobalExceptionHandler.cs` to map `PasswordHashBulkheadRejectedException` to `503 Service Unavailable` (`Retry-After: 2`).
- Create `IdentityHighLoadOptions.cs`, `BulkheadPasswordHasher.cs`, and `HybridCacheTokenRevocationService.cs` in `apps/backend/src/Infrastructure/Identity/`.
- Update `JwtTenantValidator.cs` to check `ITokenRevocationService` (resolved from `context.HttpContext.RequestServices` or constructor) when `EnableTokenRevocationCheck` is enabled, setting `TokenRevokedErrorCode = "TOKEN_REVOKED"`, and update `HandleChallengeAsync` / `TenantProblemResults.cs` to write `401 Unauthorized` ProblemDetails for `TOKEN_REVOKED`.
- Register `IdentityHighLoadOptions`, `BulkheadPasswordHasher<ApplicationUser>`, `BulkheadPasswordHasher<PlatformUser>`, and `ITokenRevocationService` (`HybridCacheTokenRevocationService`) in `IdentitySetup.AddIdentity`.
- Create `AdaptiveTraceSampler.cs` in `apps/backend/src/Infrastructure/Telemetry/`, update `TelemetryOptions.cs` (`TraceSamplingRatio = 1.0`, `AlwaysSampleSlowRequestsMs = 500`), and update `TelemetrySetup.cs` to register `SetSampler(new AdaptiveTraceSampler(telemetryOptions.TraceSamplingRatio))` and output all 6 new metrics on `GET /metrics`.
- Wire `ConfigureAppKestrel()`, `AddAppBatching(configuration)`, and `app.UseAdaptiveLoadShedding()` (right after `app.UseRouting()`) into `DependencyInjection.cs`, `Program.cs`, and `appsettings.json`.

- [ ] **Step 4: Run full backend test suite to verify 100% pass rate**

Run: `dotnet test apps/backend/ExamPrism.slnx`
Expected: PASS (all unit, architecture, and integration tests pass with 0 failures).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src apps/backend/tests
git commit -m "feat(infra): add CPU password hash bulkhead, distributed JWT revocation, and adaptive telemetry"
```

