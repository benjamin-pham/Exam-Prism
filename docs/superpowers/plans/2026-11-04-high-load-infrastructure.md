# High-Load Multi-Container Infrastructure Suite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Upgrade `ExamPrism.Infrastructure` for multi-container horizontal scaling behind a load balancer and high-concurrency exam workloads using Dual-Mode Redis/In-Memory primitives, distributed locking, L1 cache backplane, sliding-window & bulkhead rate limiting, distributed idempotency, EF Core & Wolverine resilience, Orleans load shedding, Polly v8 pipelines, and OpenTelemetry observability.

**Architecture:** Transport-agnostic contracts live in `Common.*` (`ICacheBackplane`, `IDistributedLock`, `IdempotencyEndpointExtensions`, `RateLimitPolicies`, `RequestTimeoutPolicies`, `ResiliencePipelineNames`, `ExamPrismDiagnostics`) to satisfy `ArchitectureTests`. Concrete providers in `ExamPrism.Infrastructure.*` automatically activate Redis/Replica/OTLP implementations when configured and gracefully fall back to in-memory/primary defaults in development and unit tests.

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core 10 Minimal APIs, `StackExchange.Redis` 2.11.0, `Microsoft.Extensions.Caching.Hybrid` 10.0.0, `Microsoft.Extensions.Resilience` 10.10.0, `Microsoft.Extensions.Http.Resilience` 10.10.0, `OpenTelemetry.Extensions.Hosting` 1.18.0, `OpenTelemetry.Instrumentation.AspNetCore` 1.18.0, `OpenTelemetry.Instrumentation.Http` 1.18.0, `OpenTelemetry.Instrumentation.Runtime` 1.18.0, `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.18.0, Wolverine 4.8.0, Orleans 10.4.2, EF Core 10.0.3, xUnit, FluentAssertions, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-04-high-load-infrastructure-design.md`

## Global Constraints

- Target framework `.NET 10` (`net10.0`), Central Package Management in `apps/backend/Directory.Packages.props`.
- Exact NuGet versions from local cache: `Microsoft.Extensions.Resilience` `10.10.0`, `Microsoft.Extensions.Http.Resilience` `10.10.0`, `OpenTelemetry.Extensions.Hosting` `1.18.0`, `OpenTelemetry.Instrumentation.AspNetCore` `1.18.0`, `OpenTelemetry.Instrumentation.Http` `1.18.0`, `OpenTelemetry.Instrumentation.Runtime` `1.18.0`, `OpenTelemetry.Exporter.OpenTelemetryProtocol` `1.18.0`.
- Preserve all `ArchitectureTests`: types in `Common.*` must never reference `ExamPrism.Infrastructure`, `ExamPrism.Features`, or `ExamPrism.Endpoints`; types in `ExamPrism.Features.*` must never reference `ExamPrism.Infrastructure.*` interfaces or `Microsoft.AspNetCore.{Http,Routing,Mvc}`; `IEndpoint` classes must be `public sealed`, end with `Endpoint(s)`, have no `Handle`/`HandleAsync` methods, and never reference `DbContext`.
- Dual-Mode operation: when `ConnectionStrings:Redis` is empty/whitespace, all services (`ICacheBackplane`, `IDistributedLock`, `RateLimiter`, `DataProtection`, `OutputCache`, `IdempotencyEndpointFilter`) must operate 100% in-memory without registering `IConnectionMultiplexer`.
- All Redis operations at runtime (`RedisCacheBackplane`, `RedisDistributedLock`, `RedisSlidingWindowRateLimiter`, `RedisXmlRepository`) must catch `RedisException` and `TimeoutException` and fail open with a `LogWarning`.

## Review Focus

1. **Self-eviction loop on `RedisCacheBackplane`**: When Container A publishes a key or tag invalidation to Redis Pub/Sub, Container A also receives its own message on the subscribed channel; `RedisCacheBackplane` must compare `senderInstanceId == InstanceId` and ignore self-originated messages so it does not trigger redundant evictions or infinite loops (tested in Task 1).
2. **Stale container releasing another container's `RedisDistributedLock`**: If Container A's lock expires before `DisposeAsync()` runs and Container B acquires the same lock key, a naive `DEL` from Container A would delete Container B's active lock; `RedisDistributedLock` must verify `redis.call('GET', KEYS[1]) == ARGV[1]` via Lua script before deleting (tested in Task 1).
3. **K8s liveness probe killing pods during transient DB/Redis slowdowns or failing to drain before termination**: `/health/live` must never invoke `HealthCheckService` (always `200 OK` inline), while `/health/ready` and `/health` must immediately return `503 ServiceUnavailable` when `HostDrainState.IsDraining` is true even if all dependencies are healthy (tested in Task 2).
4. **Idempotency key reuse with mutated request body or concurrent duplicate in-flight requests**: `IdempotencyEndpointFilter` must return `422 Unprocessable Entity` when the same `Idempotency-Key` is sent with a different SHA-256 body hash, and return `409 Conflict` with `Retry-After: 1` when `IDistributedLock.TryAcquireAsync` returns `null` for an in-flight request (tested in Task 3).
5. **Thread-pool starvation in `TenantWolverineMiddleware.BeforeAsync` and EF Core `EnableRetryOnFailure` breaking manual transactions**: `TenantWolverineMiddleware.BeforeAsync` must `await store.GetAsync(...)` asynchronously (zero `.GetAwaiter().GetResult()`), and `PersistenceOptions.MaxRetryCount` must default to `0` in `CustomWebApplicationFactory` (or only enable when configured) so tests using explicit `BeginTransactionAsync` do not fail with Npgsql's `NpgsqlRetryingExecutionStrategy` conflict (tested in Task 4).

---

### Task 1: NuGet Packages, `ExamPrismDiagnostics`, Shared DataProtection (`RedisXmlRepository`), `ICacheBackplane` & `IDistributedLock`

**Files:**
- Modify: `apps/backend/Directory.Packages.props`
- Modify: `apps/backend/src/ExamPrism.csproj`
- Create: `apps/backend/src/Common/Telemetry/ExamPrismDiagnostics.cs`
- Create: `apps/backend/src/Common/Caching/ICacheBackplane.cs`
- Create: `apps/backend/src/Common/Locking/IDistributedLock.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/RedisXmlRepository.cs`
- Create: `apps/backend/src/Infrastructure/Caching/RedisCacheBackplane.cs`
- Create: `apps/backend/src/Infrastructure/Locking/LockingOptions.cs`
- Create: `apps/backend/src/Infrastructure/Locking/InMemoryDistributedLock.cs`
- Create: `apps/backend/src/Infrastructure/Locking/RedisDistributedLock.cs`
- Create: `apps/backend/src/Infrastructure/Locking/LockingSetup.cs`
- Modify: `apps/backend/src/Infrastructure/Caching/CacheOptions.cs`
- Modify: `apps/backend/src/Infrastructure/Caching/HybridCacheService.cs`
- Modify: `apps/backend/src/Infrastructure/Caching/CachingSetup.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/MultiTenancySetup.cs`
- Modify: `apps/backend/src/Infrastructure/StartupTasksExtensions.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/ScaleOutPrimitivesTests.cs`

**Interfaces:**
- Consumes: `StackExchange.Redis.IConnectionMultiplexer`, `Microsoft.Extensions.Caching.Hybrid.HybridCache`, `Microsoft.AspNetCore.DataProtection.Repositories.IXmlRepository`
- Produces:
  - `Common.Telemetry.ExamPrismDiagnostics` (`ActivitySource`, `Meter`, `RecordSlowQuery()`, `RecordCacheBackplaneEviction()`, `RecordIdempotencyReplay()`, `RecordIdempotencyConflict()`, and snapshot getters `SlowQueriesCount`, `CacheBackplaneEvictionsCount`, `IdempotencyReplaysCount`, `IdempotencyConflictsCount`)
  - `Common.Caching.ICacheBackplane` (`string InstanceId`, `ValueTask PublishKeyInvalidationAsync(string scopedKey, CancellationToken ct = default)`, `ValueTask PublishTagInvalidationAsync(string scopedTag, CancellationToken ct = default)`)
  - `Common.Locking.IDistributedLock` (`ValueTask<IDistributedLockHandle?> TryAcquireAsync(string resource, TimeSpan? expiry = null, CancellationToken cancellationToken = default)`)
  - `ExamPrism.Infrastructure.Locking.LockingSetup.AddLocking(this IServiceCollection services, IConfiguration configuration, bool useRedis)`

- [ ] **Step 1: Write the failing test in `apps/backend/tests/ExamPrism.Tests/Infrastructure/ScaleOutPrimitivesTests.cs`**

Create `ScaleOutPrimitivesTests` (`namespace ExamPrism.Tests.Infrastructure`) testing:
- `RedisCacheBackplane_PublishesKeyAndTagInvalidations_EvictsRemoteL1_AndIgnoresSelfMessages`: mocks `IConnectionMultiplexer` and `ISubscriber`, captures the `Action<RedisChannel, RedisValue>` handler registered in `StartAsync`, verifies `PublishKeyInvalidationAsync("t:1:k1")` publishes `"{InstanceId}|K|t:1:k1"`, invokes the captured handler with a remote `otherInstance|K|t:1:k1` and asserts L1 entry is evicted and `ExamPrismDiagnostics.CacheBackplaneEvictionsCount` increments, while invoking with `{InstanceId}|K|t:1:k2` is ignored.
- `InMemoryDistributedLock_And_RedisDistributedLock_EnforceMutualExclusion_AndVerifyOwnerTokenOnRelease`: verifies `InMemoryDistributedLock` returns `null` when a second caller tries to acquire an active lock and succeeds after `DisposeAsync()`; verifies `RedisDistributedLock` calls `StringSetAsync` with `When.NotExists` and executes the token-checking Lua `ScriptEvaluateAsync` on `DisposeAsync()`.
- `RedisXmlRepository_StoresAndReadsDataProtectionXmlElements_AndFailsOpenOnRedisException`: mocks `IDatabase.ListRange` and `ListRightPush`, verifies `StoreElement` and `GetAllElements` round-trip `<key id="1" />`, and returns empty collection without throwing when `ListRange` throws `RedisConnectionException`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ScaleOutPrimitivesTests"`
Expected: FAIL (types do not exist yet).

- [ ] **Step 3: Add NuGet packages and implement `ExamPrismDiagnostics`, `ICacheBackplane`, `IDistributedLock`, and `RedisXmlRepository`**

- In `apps/backend/Directory.Packages.props` and `apps/backend/src/ExamPrism.csproj`, add references for:
  - `Microsoft.Extensions.Resilience` (`10.10.0`)
  - `Microsoft.Extensions.Http.Resilience` (`10.10.0`)
  - `OpenTelemetry.Extensions.Hosting` (`1.18.0`)
  - `OpenTelemetry.Instrumentation.AspNetCore` (`1.18.0`)
  - `OpenTelemetry.Instrumentation.Http` (`1.18.0`)
  - `OpenTelemetry.Instrumentation.Runtime` (`1.18.0`)
  - `OpenTelemetry.Exporter.OpenTelemetryProtocol` (`1.18.0`)
- Implement `ExamPrismDiagnostics`, `ICacheBackplane`, `NoOpCacheBackplane`, `RedisCacheBackplane`, `IDistributedLock`, `InMemoryDistributedLock`, `RedisDistributedLock`, `LockingSetup`, and `RedisXmlRepository`.
- Wire `ICacheBackplane` into `HybridCacheService`, `CatalogMultiTenantStore`, and `CachingSetup`.
- Wire `SetApplicationName("ExamPrism")` and `RedisXmlRepository` (when Redis is registered) into `MultiTenancySetup.AddMultiTenancy`.
- Wrap `StartupTasksExtensions.InitializeInfrastructureAsync` with `IDistributedLock.TryAcquireAsync("startup:infrastructure-init", TimeSpan.FromMinutes(5), cancellationToken)`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ScaleOutPrimitivesTests|FullyQualifiedName~HybridCacheServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/Directory.Packages.props apps/backend/src/ExamPrism.csproj apps/backend/src/Common apps/backend/src/Infrastructure apps/backend/tests/ExamPrism.Tests/Infrastructure/ScaleOutPrimitivesTests.cs
git commit -m "feat(infra): add distributed lock, L1 cache backplane, and shared data protection"
```

---

### Task 2: Load Balancer `ReverseProxySetup`, Liveness/Readiness Split (`/health/live`, `/health/ready`) & Graceful Shutdown (`HostDrainState`)

**Files:**
- Create: `apps/backend/src/Infrastructure/Http/ReverseProxyOptions.cs`
- Create: `apps/backend/src/Infrastructure/Http/ReverseProxySetup.cs`
- Create: `apps/backend/src/Infrastructure/Health/GracefulShutdownOptions.cs`
- Create: `apps/backend/src/Infrastructure/Health/HostDrainState.cs`
- Create: `apps/backend/src/Infrastructure/Health/GracefulShutdownHostedService.cs`
- Create: `apps/backend/src/Infrastructure/Health/HealthSetup.cs`
- Modify: `apps/backend/src/Endpoints/Health/GetHealthEndpoint.cs`
- Modify: `apps/backend/src/Infrastructure/RateLimiting/RateLimitingSetup.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/EdgeAndHealthTests.cs`

**Interfaces:**
- Consumes: `Microsoft.AspNetCore.Builder.ForwardedHeadersOptions`, `Microsoft.Extensions.Hosting.IHostApplicationLifetime`
- Produces:
  - `ExamPrism.Infrastructure.Http.ReverseProxySetup` (`AddAppReverseProxy`, `UseAppReverseProxy`, `ResolveClientIp(HttpContext)`)
  - `ExamPrism.Infrastructure.Health.HostDrainState` (`bool IsDraining`, `void MarkDraining()`)
  - `ExamPrism.Infrastructure.Health.HealthSetup.AddAppHealthDrain(this IServiceCollection services, IConfiguration configuration)`
  - `GET /health/live`, `GET /health/ready`, and `GET /health` in `GetHealthEndpoint`

- [ ] **Step 1: Write the failing test in `apps/backend/tests/ExamPrism.Tests/Infrastructure/EdgeAndHealthTests.cs`**

Create `EdgeAndHealthTests` (`namespace ExamPrism.Tests.Infrastructure`) testing:
- `ReverseProxySetup_ResolvesClientIpFromCfConnectingIp_XRealIp_AndForwardedFor`: verifies `ReverseProxySetup.ResolveClientIp` prioritizes `CF-Connecting-IP`, then `X-Real-IP`, then `Connection.RemoteIpAddress`, and configures `ForwardedHeadersOptions` with private CIDR networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`).
- `HealthEndpoints_LiveAlwaysReturns200_WhileReadyAndHealthReturn503WhenHostIsDraining`: uses `CustomWebApplicationFactory` (or `HostDrainState` unit/integration check) to verify `/health/live` returns `200 OK` even when `HostDrainState.IsDraining` is `true`, whereas `/health/ready` and `/health` return `503 ServiceUnavailable` immediately when `HostDrainState.IsDraining` is `true`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~EdgeAndHealthTests"`
Expected: FAIL (`ReverseProxySetup` and `HostDrainState` do not exist yet).

- [ ] **Step 3: Implement `ReverseProxySetup`, `HostDrainState`, `GracefulShutdownHostedService`, and update `GetHealthEndpoint`**

- Create `ReverseProxyOptions` and `ReverseProxySetup` in `apps/backend/src/Infrastructure/Http/`.
- Create `GracefulShutdownOptions`, `HostDrainState`, `GracefulShutdownHostedService`, and `HealthSetup` in `apps/backend/src/Infrastructure/Health/`.
- Update `GetHealthEndpoint.MapEndpoint` to map `/health/live` (inline `200 OK` returning `new GetHealthResult("Healthy", DateTime.UtcNow, true)`), `/health/ready`, and `/health` (checking `HostDrainState.IsDraining` before invoking `GetHealthQuery`).
- Update `RateLimitingSetup.GetClientIp` to delegate to `ReverseProxySetup.ResolveClientIp(context)`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~EdgeAndHealthTests|FullyQualifiedName~HealthEndpointTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/Http apps/backend/src/Infrastructure/Health apps/backend/src/Endpoints/Health/GetHealthEndpoint.cs apps/backend/src/Infrastructure/RateLimiting/RateLimitingSetup.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/EdgeAndHealthTests.cs
git commit -m "feat(infra): add reverse proxy IP resolution, liveness/readiness split, and graceful drain"
```

---

### Task 3: Sliding Window Rate Limiter, Concurrency Bulkheads, HTTP Timeouts/Compression/OutputCache & Distributed `IdempotencyEndpointFilter`

**Files:**
- Create: `apps/backend/src/Common/Http/RequestTimeoutPolicies.cs`
- Create: `apps/backend/src/Common/Idempotency/IdempotencyEndpointExtensions.cs`
- Create: `apps/backend/src/Infrastructure/RateLimiting/InMemorySlidingWindowRateLimiter.cs`
- Create: `apps/backend/src/Infrastructure/RateLimiting/RedisSlidingWindowRateLimiter.cs`
- Create: `apps/backend/src/Infrastructure/Http/HttpPerformanceOptions.cs`
- Create: `apps/backend/src/Infrastructure/Http/HttpPerformanceSetup.cs`
- Create: `apps/backend/src/Infrastructure/Idempotency/IdempotencyOptions.cs`
- Create: `apps/backend/src/Infrastructure/Idempotency/IdempotencyEndpointFilter.cs`
- Create: `apps/backend/src/Infrastructure/Idempotency/IdempotencySetup.cs`
- Modify: `apps/backend/src/Common/RateLimiting/RateLimitPolicies.cs`
- Modify: `apps/backend/src/Infrastructure/RateLimiting/RateLimitingOptions.cs`
- Modify: `apps/backend/src/Infrastructure/RateLimiting/RateLimitingSetup.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/TrafficControlAndIdempotencyTests.cs`

**Interfaces:**
- Consumes: `Common.Caching.ICacheService`, `Common.Locking.IDistributedLock`, `Common.Telemetry.ExamPrismDiagnostics`, `System.Threading.RateLimiting.RateLimiter`
- Produces:
  - `Common.RateLimiting.RateLimitPolicies` (`Auth`, `ExamCritical`, `HeavyOperationBulkhead`)
  - `Common.Http.RequestTimeoutPolicies` (`Default`, `Fast`, `ExamSubmit`, `LongRunning`)
  - `Common.Idempotency.IdempotencyEndpointExtensions.RequireIdempotency<TBuilder>(this TBuilder builder)`
  - `ExamPrism.Infrastructure.RateLimiting.RedisSlidingWindowRateLimiter` & `InMemorySlidingWindowRateLimiter`
  - `ExamPrism.Infrastructure.Http.HttpPerformanceSetup` (`AddAppHttpPerformance`, `UseAppHttpPerformance`)
  - `ExamPrism.Infrastructure.Idempotency.IdempotencySetup` (`AddAppIdempotency`)

- [ ] **Step 1: Write the failing test in `apps/backend/tests/ExamPrism.Tests/Infrastructure/TrafficControlAndIdempotencyTests.cs`**

Create `TrafficControlAndIdempotencyTests` (`namespace ExamPrism.Tests.Infrastructure`) testing:
- `InMemorySlidingWindowRateLimiter_And_RedisSlidingWindowRateLimiter_EnforceSlidingWindowAndFailOpen`: verifies `InMemorySlidingWindowRateLimiter` blocks after `permitLimit` and smoothly recovers permits across window segments using `FakeTimeProvider`; verifies `RedisSlidingWindowRateLimiter` executes the ZSET Lua script, parses `{ 1, remaining, 0 }` / `{ 0, 0, retryMs }`, and fails open when `ScriptEvaluateAsync` throws `RedisTimeoutException`.
- `IdempotencyEndpointFilter_ReplaysMatchingPayload_RejectsMutatedPayloadWith422_AndRejectsConcurrentInFlightWith409`: invokes `IdempotencyEndpointFilter.InvokeAsync` with an `Idempotency-Key` header:
  1. First call executes `next` once and caches the result.
  2. Second call with identical JSON body returns cached response with `X-Idempotency-Replayed: true` without invoking `next`.
  3. Third call with same `Idempotency-Key` but different JSON body returns `422 Unprocessable Entity` (`ProblemDetails`).
  4. Concurrent call when `IDistributedLock.TryAcquireAsync` returns `null` returns `409 Conflict` with `Retry-After: 1`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TrafficControlAndIdempotencyTests"`
Expected: FAIL (types do not exist yet).

- [ ] **Step 3: Implement Sliding Window Rate Limiters, Concurrency Bulkhead Policies, HTTP Performance Setup, and `IdempotencyEndpointFilter`**

- Update `RateLimitPolicies.cs` (`ExamCritical = "ExamCritical"`, `HeavyOperationBulkhead = "HeavyOperationBulkhead"`), `RateLimitingOptions.cs`, and `RateLimitingSetup.cs` (adding `ExamCritical` policy with `RedisSlidingWindowRateLimiter`/`InMemorySlidingWindowRateLimiter`, `HeavyOperationBulkhead` policy with `RateLimitPartition.GetConcurrencyLimiter`, and exempting `/metrics` in `GlobalLimiter`).
- Implement `InMemorySlidingWindowRateLimiter` and `RedisSlidingWindowRateLimiter`.
- Implement `RequestTimeoutPolicies`, `HttpPerformanceOptions`, and `HttpPerformanceSetup` (`AddRequestTimeouts`, `AddResponseCompression` with Brotli + Gzip, `AddRequestDecompression`, `AddOutputCache` with `"TenantScopedCache"` policy).
- Implement `IdempotencyEndpointExtensions`, `IdempotencyOptions`, `IdempotencyEndpointFilter`, and `IdempotencySetup`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TrafficControlAndIdempotencyTests|FullyQualifiedName~RateLimiterUnitTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common apps/backend/src/Infrastructure apps/backend/tests/ExamPrism.Tests/Infrastructure/TrafficControlAndIdempotencyTests.cs
git commit -m "feat(infra): add sliding window rate limiter, bulkhead policies, HTTP compression/timeouts, and idempotency filter"
```

---

### Task 4: Persistence Resilience (`SlowQueryTelemetryInterceptor`, Read-Replica Routing), Async `TenantWolverineMiddleware`, Orleans High-Load Tuning, Polly v8 & OpenTelemetry Suite

**Files:**
- Create: `apps/backend/src/Common/Resilience/ResiliencePipelineNames.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/PersistenceOptions.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/SlowQueryTelemetryInterceptor.cs`
- Create: `apps/backend/src/Infrastructure/Resilience/ResilienceOptions.cs`
- Create: `apps/backend/src/Infrastructure/Resilience/ResilienceSetup.cs`
- Create: `apps/backend/src/Infrastructure/Telemetry/TelemetryOptions.cs`
- Create: `apps/backend/src/Infrastructure/Telemetry/TelemetrySetup.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/MultiTenancyOptions.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/TenantConnectionStringNormalizer.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/TenantWolverineMiddleware.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/MultiTenancySetup.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/PersistenceSetup.cs`
- Modify: `apps/backend/src/Infrastructure/Orleans/OrleansOptions.cs`
- Modify: `apps/backend/src/Infrastructure/Orleans/OrleansSetup.cs`
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs`
- Modify: `apps/backend/src/Program.cs`
- Modify: `apps/backend/src/appsettings.json`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/ResiliencePersistenceAndTelemetryTests.cs`

**Interfaces:**
- Consumes: `Common.Telemetry.ExamPrismDiagnostics`, `Polly.Registry.ResiliencePipelineProvider<string>`
- Produces:
  - `Common.Resilience.ResiliencePipelineNames.DefaultIo` (`"default-io"`)
  - `ExamPrism.Infrastructure.Persistence.SlowQueryTelemetryInterceptor`
  - `ExamPrism.Infrastructure.MultiTenancy.ITenantConnectionStringNormalizer.NormalizeForReadReplica(string connectionString, string? replicaHostOverride = null)`
  - `ExamPrism.Infrastructure.Resilience.ResilienceSetup.AddAppResilience`
  - `ExamPrism.Infrastructure.Telemetry.TelemetrySetup` (`AddAppTelemetry`, `UseAppTelemetry` exposing `GET /metrics`)

- [ ] **Step 1: Write the failing test in `apps/backend/tests/ExamPrism.Tests/Infrastructure/ResiliencePersistenceAndTelemetryTests.cs`**

Create `ResiliencePersistenceAndTelemetryTests` (`namespace ExamPrism.Tests.Infrastructure`) testing:
- `TenantConnectionStringNormalizer_AppliesNoResetOnClose_KeepAlive_AndReadReplicaHostOverride`: verifies `Normalize` sets `NoResetOnClose = true` and `KeepAlive = 30`, and `NormalizeForReadReplica` rewrites `Host=replica.db.internal;Target Session Attributes=prefer-standby` when `replicaHostOverride` is provided or falls back to primary when blank.
- `SlowQueryTelemetryInterceptor_LogsWarning_AndIncrementsMetricWhenDurationExceedsThreshold`: triggers `SlowQueryTelemetryInterceptor.RecordIfSlow(commandText: "SELECT * FROM exams", duration: TimeSpan.FromMilliseconds(600))` with threshold `500ms` and asserts `ExamPrismDiagnostics.SlowQueriesCount` increments and logger receives `LogLevel.Warning`.
- `TenantWolverineMiddleware_BeforeAsync_AwaitsStoreAsynchronously`: verifies `BeforeAsync` resolves tenant asynchronously and sets `IMultiTenantContextSetter`.
- `ResilienceAndTelemetry_RegisterDefaultIoPipeline_AndExposePrometheusMetricsEndpoint`: resolves `ResiliencePipelineProvider<string>` from DI and executes `GetPipeline(ResiliencePipelineNames.DefaultIo)`, and calls `GET /metrics` on `CustomWebApplicationFactory` asserting `200 OK` containing `examprism_db_slow_queries_total` and `examprism_idempotency_replays_total`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ResiliencePersistenceAndTelemetryTests"`
Expected: FAIL (types and methods do not exist yet).

- [ ] **Step 3: Implement Persistence Resilience, Async Wolverine Middleware, Orleans Tuning, Polly v8, OpenTelemetry, and wire into `DependencyInjection.cs` & `Program.cs`**

- Update `MultiTenancyOptions.cs` (`NoResetOnClose = true`, `KeepAliveSeconds = 30`) and `TenantConnectionStringNormalizer.cs` (adding `NormalizeForReadReplica`).
- Create `PersistenceOptions.cs` and `SlowQueryTelemetryInterceptor.cs`, and wire them into `PersistenceSetup.cs` (apply `CommandTimeout`, `MaxBatchSize`, and `EnableRetryOnFailure` when `MaxRetryCount > 0`). In `CustomWebApplicationFactory.cs`, set `"Persistence:MaxRetryCount": "0"` so test fixtures using manual transactions remain compatible.
- Update `TenantWolverineMiddleware.BeforeAsync` to `public async Task BeforeAsync(...)` using `await store.GetAsync(...)` and `await store.GetByIdentifierAsync(...)`, and add transient `NpgsqlException` / `TimeoutException` retry policies in `MultiTenancySetup.UseWolverine`.
- Update `OrleansOptions.cs` and `OrleansSetup.cs` with `AdvertisedIpAddress`, `LoadSheddingOptions`, `GrainCollectionOptions`, `SiloMessagingOptions`, and `siloBuilder.AddActivityPropagation()`.
- Create `ResiliencePipelineNames.cs`, `ResilienceOptions.cs`, and `ResilienceSetup.cs`.
- Create `TelemetryOptions.cs` and `TelemetrySetup.cs` (`AddOpenTelemetry().WithTracing(...).WithMetrics(...)` + `GET /metrics` endpoint).
- Wire all new setup methods into `DependencyInjection.cs`, `Program.cs`, and `appsettings.json`.

- [ ] **Step 4: Run full test suite to verify all unit, architecture, and integration tests pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ResiliencePersistenceAndTelemetryTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src apps/backend/tests
git commit -m "feat(infra): add persistence resilience, async wolverine middleware, orleans load shedding, polly v8, and opentelemetry"
```

