# Design Specification: High-Load Multi-Container Infrastructure Suite

**Date:** 2026-11-04  
**Status:** Approved  
**Scope:** Architectural Subsystem  

---

## 1. Context and Goals

ExamPrism is a multi-tenant online examination platform built on .NET 10 Minimal APIs, Wolverine, Finbuckle.MultiTenant (database-per-tenant + shared catalog), HybridCache, StackExchange.Redis, and Microsoft Orleans.

When deployed behind a Load Balancer (Nginx, Envoy, AWS ALB, Cloudflare, or Kubernetes Ingress) across $N$ stateless/stateful container replicas under heavy exam concurrency, the infrastructure must guarantee:

1. **Multi-Container State Consistency:**
   * Shared **DataProtection Key Ring** across all containers so encrypted tenant connection strings in `CatalogDbContext` can be decrypted by any replica.
   * **Redis Pub/Sub L1 Cache Backplane** so key/tag invalidations in `HybridCacheService` and `CatalogMultiTenantStore` evict stale L1 memory cache entries across all peer containers in real time.
   * **Distributed Locking (`IDistributedLock`)** to serialize cluster-wide startup tasks (Platform Admin seeding, stuck tenant provisioning recovery, and tenant fleet migrations) and critical domain mutations.
   * **Reverse Proxy / Load Balancer IP Resolution (`ForwardedHeaders`)** with configurable CIDR `KnownNetworks` and `KnownProxies` so per-IP rate limiting never collapses all users into the load balancer's internal IP.
   * **Liveness (`/health/live`) vs. Readiness (`/health/ready`) Split + Graceful Shutdown Drain (`HostDrainState`)** for zero-downtime rolling deployments.

2. **Traffic Control, Load Shedding & Idempotency:**
   * **Sliding Window Rate Limiter** (atomic Redis Lua script + in-memory fallback) alongside existing Fixed Window limiters to prevent boundary burst doubling.
   * **Concurrency Bulkhead Policies** (`ExamCritical` vs. `HeavyOperationBulkhead`) to isolate slow/admin operations from starving exam submission threads and DB pools.
   * **Request Timeouts**, **Brotli/Gzip Response Compression**, **Request Decompression**, and **Tenant-Scoped Output Caching**.
   * **Distributed `Idempotency-Key` Endpoint Filter** using `IDistributedLock` + `ICacheService` to prevent duplicate exam submissions or tenant mutations across retries routed to different containers.

3. **Persistence Resilience, Read-Replica Routing & Wolverine Throughput:**
   * **EF Core Resilience:** `EnableRetryOnFailure`, `CommandTimeout`, `MaxBatchSize`, and `NoResetOnClose` / keep-alive connection pool normalization.
   * **`SlowQueryTelemetryInterceptor`:** Logs warnings and increments OpenTelemetry metrics whenever SQL execution exceeds `SlowQueryThresholdMs`.
   * **Dual-Mode Read-Replica Routing (`IReadOnlyCatalogDbContext` / `IReadOnlyAppDbContext`):** Routes read-only queries with `QueryTrackingBehavior.NoTracking` to replica connection strings when configured, falling back to primary connection strings in Dev/Test.
   * **Async Wolverine Middleware & Transient Retry:** Eliminates thread-pool-starving `.GetAwaiter().GetResult()` sync-over-async calls in `TenantWolverineMiddleware.BeforeAsync` and configures exponential backoff retry policies for transient infrastructure exceptions.

4. **Orleans High-Load Tuning, Polly v8 Resilience & OpenTelemetry Observability:**
   * **Orleans Silo Hardening:** Multi-container `AdvertisedIpAddress` support, CPU-based `LoadSheddingOptions`, faster `GrainCollectionOptions` (`15m` default age), `SiloMessagingOptions` timeouts, and `AddActivityPropagation()` for distributed grain tracing.
   * **Polly v8 Resilience Pipelines (`Microsoft.Extensions.Resilience`):** Standardized `"default-io"` pipeline (Retry + Circuit Breaker + Timeout) and standard HTTP client resilience handlers.
   * **OpenTelemetry Suite (`ExamPrism.Infrastructure.Telemetry`):** Distributed Tracing + Metrics across ASP.NET Core, HttpClient, Npgsql, Redis, Wolverine, Orleans, and custom `ExamPrismDiagnostics` counters/histograms with Prometheus (`/metrics`) and optional OTLP export.

All subsystems follow the **Adaptive Dual-Mode** pattern: when Redis, Read-Replicas, or OTLP endpoints are configured, distributed production providers activate; when absent in local development or automated tests, deterministic in-memory/primary fallbacks run seamlessly without breaking existing tests.

---

## 2. Architecture & Layering Rules

To comply with `ArchitectureTests`:
* **`Common.*` (`apps/backend/src/Common`)**: Houses all public abstractions, attributes, policies, and contracts (`ICacheBackplane`, `IDistributedLock`, `IIdempotencyService`, `IdempotencyEndpointExtensions`, `RateLimitPolicies`, `RequestTimeoutPolicies`, `ResiliencePipelineNames`, `ExamPrismDiagnostics`). Must **never** reference `ExamPrism.Infrastructure`, `ExamPrism.Features`, or `ExamPrism.Endpoints`.
* **`ExamPrism.Infrastructure.*` (`apps/backend/src/Infrastructure`)**: Houses all implementations, Redis Lua scripts, EF Core interceptors, middleware, OpenTelemetry setup, and DI registration extensions.
* **`ExamPrism.Features.*` (`apps/backend/src/Features`)**: Handlers must remain `public sealed`, end with `Handler`, use non-defaulted constructor parameters, and never reference ASP.NET Core HTTP types or Infrastructure interfaces.
* **`ExamPrism.Endpoints.*` (`apps/backend/src/Endpoints`)**: Endpoints must remain `public sealed`, implement `IEndpoint`, execute inline in `MapEndpoint`, and never inject `DbContext`.

---

## 3. Configuration Schema (`appsettings.json`)

```json
{
  "ReverseProxy": {
    "Enabled": true,
    "ForwardLimit": 2,
    "TrustAllPrivateNetworks": true,
    "KnownProxies": [],
    "KnownNetworks": [ "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" ]
  },
  "GracefulShutdown": {
    "ShutdownTimeoutSeconds": 30,
    "DrainDelaySeconds": 0
  },
  "Caching": {
    "KeyPrefix": "examprism:",
    "DefaultExpirationMinutes": 30,
    "DefaultLocalExpirationMinutes": 5,
    "EnableBackplane": true,
    "BackplaneChannelName": "cache:backplane",
    "OutputCacheExpirationSeconds": 30
  },
  "Locking": {
    "KeyPrefix": "lock:",
    "DefaultExpirySeconds": 30
  },
  "Idempotency": {
    "HeaderName": "Idempotency-Key",
    "ExpirationMinutes": 1440,
    "LockTimeoutSeconds": 30
  },
  "RateLimiting": {
    "Global": {
      "PermitLimit": 100,
      "WindowSeconds": 60
    },
    "Auth": {
      "PermitLimit": 10,
      "WindowSeconds": 60
    },
    "ExamCritical": {
      "PermitLimit": 300,
      "WindowSeconds": 60,
      "SegmentsPerWindow": 6
    },
    "HeavyOperationBulkhead": {
      "PermitLimit": 10,
      "QueueLimit": 5
    }
  },
  "RequestTimeouts": {
    "DefaultTimeoutSeconds": 30,
    "FastTimeoutSeconds": 5,
    "ExamSubmitTimeoutSeconds": 15,
    "LongRunningTimeoutSeconds": 120
  },
  "Persistence": {
    "CommandTimeoutSeconds": 30,
    "MaxBatchSize": 100,
    "MaxRetryCount": 3,
    "MaxRetryDelaySeconds": 5,
    "SlowQueryThresholdMs": 500,
    "CatalogReadConnection": "",
    "TenantReadReplicaHost": ""
  },
  "MultiTenancy": {
    "DatabasePool": {
      "MaxPoolSize": 15,
      "MinPoolSize": 0,
      "ConnectionIdleLifetimeSeconds": 30,
      "ConnectionPruningIntervalSeconds": 10,
      "NoResetOnClose": true,
      "KeepAliveSeconds": 30
    }
  },
  "Orleans": {
    "ClusterId": "examprism-cluster",
    "ServiceId": "examprism-service",
    "StateStoreName": "Default",
    "SiloPort": 11111,
    "GatewayPort": 30000,
    "AdvertisedIpAddress": "",
    "LoadSheddingEnabled": true,
    "LoadSheddingLimitPercent": 95,
    "CollectionAgeMinutes": 15,
    "CollectionQuantumMinutes": 1,
    "ResponseTimeoutSeconds": 15
  },
  "Resilience": {
    "MaxRetryAttempts": 3,
    "BaseDelayMs": 100,
    "CircuitBreakerFailureRatio": 0.5,
    "CircuitBreakerSamplingDurationSeconds": 10,
    "CircuitBreakerBreakDurationSeconds": 15,
    "AttemptTimeoutSeconds": 10
  },
  "Telemetry": {
    "ServiceName": "ExamPrism.Backend",
    "EnablePrometheusEndpoint": true,
    "OtlpEndpoint": ""
  }
}
```

---

## 4. Detailed Subsystem Designs

### 4.1. Multi-Container Scale-Out & Edge Hardening

#### 4.1.1. Shared DataProtection Key Ring
* **Package:** `Microsoft.AspNetCore.DataProtection.StackExchangeRedis`.
* **Setup (`MultiTenancySetup.AddMultiTenancy`):**
  * Always configures `services.AddDataProtection().SetApplicationName("ExamPrism")`.
  * When `ConnectionStrings:Redis` is configured (`!string.IsNullOrWhiteSpace(redisConnectionString)`), resolves the singleton `IConnectionMultiplexer` and calls `.PersistKeysToStackExchangeRedis(multiplexer, $"{cachePrefix}dataprotection:keys")`.
  * When Redis is absent, retains default local key storage so unit/integration tests require no Redis container unless testing Redis specifically.

#### 4.1.2. HybridCache Redis Pub/Sub Backplane (`Common.Caching` & `ExamPrism.Infrastructure.Caching`)
* **Abstraction (`Common/Caching/ICacheBackplane.cs`):**
  ```csharp
  namespace Common.Caching;

  public interface ICacheBackplane
  {
      string InstanceId { get; }
      ValueTask PublishKeyInvalidationAsync(string scopedKey, CancellationToken cancellationToken = default);
      ValueTask PublishTagInvalidationAsync(string scopedTag, CancellationToken cancellationToken = default);
  }
  ```
* **Implementations:**
  * `NoOpCacheBackplane`: Used when Redis is disabled or `CacheOptions.EnableBackplane == false`.
  * `RedisCacheBackplane` (`IHostedService`, `ICacheBackplane`, `IDisposable`):
    * Subscribes on startup (`StartAsync`) to Redis Pub/Sub channel `RedisChannel.Literal($"{_cacheOptions.KeyPrefix}{_cacheOptions.BackplaneChannelName}")`.
    * Payload format: `"{InstanceId}|K|{scopedKey}"` for single-key eviction, `"{InstanceId}|T|{scopedTag}"` for tag eviction.
    * When a message arrives: parses `senderInstanceId`, `kind` (`K` or `T`), and `target`. If `senderInstanceId == InstanceId`, ignores the message. Otherwise invokes `_hybridCache.RemoveAsync(target)` or `_hybridCache.RemoveByTagAsync(target)` and increments `ExamPrismDiagnostics.CacheBackplaneEvictionsCounter`.
    * Wraps all publish and subscribe operations in `try/catch` (fail-open, logs `Warning` on Redis transient disconnects).
* **Integration:**
  * `HybridCacheService.RemoveAsync` and `RemoveByTagAsync` invoke `_backplane.PublishKeyInvalidationAsync(scopedKey)` and `_backplane.PublishTagInvalidationAsync(scopedTag)` after removing from `_hybridCache`.
  * `CatalogMultiTenantStore.InvalidateTenantAsync` invokes `_backplane.PublishKeyInvalidationAsync` and `_backplane.PublishTagInvalidationAsync` for all invalidated tenant keys and tags.

#### 4.1.3. Distributed Locking (`Common.Locking` & `ExamPrism.Infrastructure.Locking`)
* **Abstraction (`Common/Locking/IDistributedLock.cs`):**
  ```csharp
  namespace Common.Locking;

  public interface IDistributedLockHandle : IAsyncDisposable
  {
      string Resource { get; }
      string LockToken { get; }
  }

  public interface IDistributedLock
  {
      ValueTask<IDistributedLockHandle?> TryAcquireAsync(
          string resource,
          TimeSpan? expiry = null,
          CancellationToken cancellationToken = default);
  }
  ```
* **Implementations (`ExamPrism.Infrastructure.Locking`):**
  * `RedisDistributedLock`:
    * Uses `IDatabase.StringSetAsync(fullKey, token, expiry, When.NotExists)`.
    * On `DisposeAsync()`, executes atomic Lua script:
      ```lua
      if redis.call('GET', KEYS[1]) == ARGV[1] then
          return redis.call('DEL', KEYS[1])
      else
          return 0
      end
      ```
    * Fail-open option for startup vs. strict lock return: returns `null` if lock is already held by another container; if Redis connection itself fails transiently, logs warning and falls back to `InMemoryDistributedLock` so single-node recovery still works.
  * `InMemoryDistributedLock`:
    * Uses `ConcurrentDictionary<string, SemaphoreSlim>` with non-blocking `WaitAsync(0, cancellationToken)` to provide identical semantics in In-Memory mode.
* **Cluster-Safe Startup (`StartupTasksExtensions.InitializeInfrastructureAsync`):**
  * Acquires lock `"startup:infrastructure-init"` (TTL 5 minutes) via `IDistributedLock`.
  * Only the container that acquires the lock executes `InitializeIdentityAsync` and `InitializeMultiTenancyAsync`. Peer containers starting simultaneously log `"Startup initialization lock is held by another instance; skipping duplicate execution."` and proceed.

#### 4.1.4. Reverse Proxy / Load Balancer Setup (`ExamPrism.Infrastructure.Http`)
* **`ReverseProxyOptions` & `ReverseProxySetup`:**
  * Configures `ForwardedHeadersOptions` with `ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost`.
  * Applies `ForwardLimit` from configuration.
  * If `TrustAllPrivateNetworks == true`, clears default loopback restrictions (`KnownIPNetworks.Clear()`, `KnownProxies.Clear()`) or adds RFC 1918 private networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `127.0.0.0/8`, `::1/128`, `fc00::/7`) plus any explicit `KnownProxies` / `KnownNetworks` entries.
  * Updates `RateLimitingSetup.GetClientIp(HttpContext)` to check `CF-Connecting-IP` and `X-Real-IP` headers (when `ReverseProxy:Enabled` is true) before falling back to `context.Connection.RemoteIpAddress`.

#### 4.1.5. Liveness / Readiness Split & Graceful Shutdown Drain
* **`HostDrainState` (`ExamPrism.Infrastructure.Health`):**
  * Singleton state tracking `bool IsDraining`.
  * `GracefulShutdownHostedService` hooks `IHostApplicationLifetime.ApplicationStopping`, sets `hostDrainState.MarkDraining()`, and if `GracefulShutdownOptions.DrainDelaySeconds > 0`, delays shutdown so Load Balancers observe `/health/ready` returning `503` before active connections close.
* **Endpoints (`GetHealthEndpoint`):**
  * `GET /health/live`: Inline endpoint returning `200 OK` with `new GetHealthResult("Healthy", DateTime.UtcNow, true)` immediately without querying DB/Redis/Orleans (prevents K8s liveness kills during transient DB load).
  * `GET /health/ready` and `GET /health`: If `HostDrainState.IsDraining` is `true`, returns `503 ServiceUnavailable` immediately; otherwise invokes `GetHealthQuery` via Wolverine bus to verify `catalog_db`, `redis`, and `orleans`.

---

### 4.2. Traffic Control, Concurrency Bulkheads, HTTP Performance & Idempotency

#### 4.2.1. Sliding Window Rate Limiter & Concurrency Bulkhead Policies
* **`RedisSlidingWindowRateLimiter` & `InMemorySlidingWindowRateLimiter` (`ExamPrism.Infrastructure.RateLimiting`):**
  * Implements `RateLimiter` using a Redis Sorted Set (`ZSET`) Lua script:
    ```lua
    local key = KEYS[1]
    local nowMs = tonumber(ARGV[1])
    local windowMs = tonumber(ARGV[2])
    local limit = tonumber(ARGV[3])
    local member = ARGV[4]
    local clearBefore = nowMs - windowMs

    redis.call('ZREMRANGEBYSCORE', key, '-inf', clearBefore)
    local current = redis.call('ZCARD', key)
    if current < limit then
        redis.call('ZADD', key, nowMs, member)
        redis.call('PEXPIRE', key, windowMs)
        return { 1, limit - current - 1, 0 }
    else
        local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
        local retryMs = windowMs
        if oldest[2] then
            retryMs = math.max(1, (tonumber(oldest[2]) + windowMs) - nowMs)
        end
        return { 0, 0, retryMs }
    end
    ```
  * Fail-open on Redis exceptions (logs warning, permits request).
* **New Policies in `RateLimitPolicies` (`Common.RateLimiting`):**
  * `RateLimitPolicies.ExamCritical = "ExamCritical"`: Partitioned per `{scope}:exam:{userOrIp}` using `RedisSlidingWindowRateLimiter` (when Redis enabled) or `InMemorySlidingWindowRateLimiter` (when in-memory).
  * `RateLimitPolicies.HeavyOperationBulkhead = "HeavyOperationBulkhead"`: Partitioned per `{scope}:bulkhead` using `RateLimitPartition.GetConcurrencyLimiter` (`PermitLimit = 10`, `QueueLimit = 5`, `QueueProcessingOrder.OldestFirst`) to cap concurrent heavy requests per container.

#### 4.2.2. Request Timeouts, Compression & Output Caching (`ExamPrism.Infrastructure.Http`)
* **`RequestTimeoutPolicies` (`Common/Http/RequestTimeoutPolicies.cs`):**
  * Constants: `Default = "Default"`, `Fast = "Fast"`, `ExamSubmit = "ExamSubmit"`, `LongRunning = "LongRunning"`.
  * Configured via `services.AddRequestTimeouts(...)` and `app.UseRequestTimeouts()`.
* **`CompressionSetup` (`ExamPrism.Infrastructure.Http`):**
  * Registers `AddResponseCompression` (`EnableForHttps = true`, `BrotliCompressionProvider` + `GzipCompressionProvider` at `CompressionLevel.Fastest` for `application/json` and `application/problem+json`) and `AddRequestDecompression`.
* **`OutputCachingSetup` (`ExamPrism.Infrastructure.Http`):**
  * Registers `AddOutputCache` with policy `"TenantScopedCache"` that varies by `X-Tenant`, `Host`, and `Accept-Language` (`Expire(TimeSpan.FromSeconds(options.OutputCacheExpirationSeconds))`).

#### 4.2.3. Distributed Idempotency (`Common.Idempotency` & `ExamPrism.Infrastructure.Idempotency`)
* **Contracts (`Common/Idempotency`):**
  * `IdempotencyEndpointExtensions.RequireIdempotency<TBuilder>(this TBuilder builder)` attaches `IdempotencyMetadata` and `IdempotencyEndpointFilter`.
* **`IdempotencyEndpointFilter` (`IEnpointFilter` in `ExamPrism.Infrastructure.Idempotency`):**
  1. Checks for HTTP methods `POST`, `PUT`, `PATCH`. Reads header `Idempotency-Key` (default name from `IdempotencyOptions`). If absent, executes pipeline normally.
  2. Validates key length (1..128 chars); if invalid, returns `400 Bad Request` (`ProblemDetails`).
  3. Enables request buffering (`HttpRequestRewindExtensions.EnableBuffering`), computes SHA-256 hex digest of request body, and rewinds `Request.Body.Position = 0`.
  4. Builds scoped cache key: `idempotency:{tenantScope}:{userIdOrIp}:{method}:{path}:{rawKey}`.
  5. Checks `ICacheService.GetOrDefaultAsync<IdempotencyCacheEntry>(cacheKey)`:
     * If found and `entry.PayloadHash != currentHash` -> returns `422 Unprocessable Entity` (`ProblemDetails`: `"Idempotency-Key has already been used with a different request payload."`).
     * If found and hashes match -> sets header `X-Idempotency-Replayed: true`, increments `ExamPrismDiagnostics.IdempotencyReplaysCounter`, and returns cached status code + JSON payload.
  6. Acquires `IDistributedLock.TryAcquireAsync(cacheKey, TimeSpan.FromSeconds(options.LockTimeoutSeconds))`:
     * If lock is null (concurrent duplicate in-flight on this or another container) -> sets `Retry-After: 1` and returns `409 Conflict` (`ProblemDetails`: `"A request with the same Idempotency-Key is currently being processed."`).
  7. Executes `await next(context)`. If the result produces a `2xx` status code (or serializable value), stores `IdempotencyCacheEntry` in `ICacheService` for `ExpirationMinutes` and returns the response.

---

### 4.3. Persistence Resilience, Read-Replica Routing & Wolverine Throughput

#### 4.3.1. EF Core Resilience & `SlowQueryTelemetryInterceptor` (`ExamPrism.Infrastructure.Persistence`)
* **`PersistenceOptions`:** Configures `CommandTimeoutSeconds`, `MaxBatchSize`, `MaxRetryCount`, `MaxRetryDelaySeconds`, `SlowQueryThresholdMs`, `CatalogReadConnection`, `TenantReadReplicaHost`.
* **`SlowQueryTelemetryInterceptor` (`DbCommandInterceptor`):**
  * Overrides `ReaderExecutedAsync`, `NonQueryExecutedAsync`, and `ScalarExecutedAsync`.
  * Inspects `eventData.Duration.TotalMilliseconds`. If `>= SlowQueryThresholdMs`, logs a structured warning with `ElapsedMs`, `CommandText`, and increments `ExamPrismDiagnostics.SlowQueriesCounter`.
* **`PersistenceSetup.AddPersistence`:**
  * Configures Npgsql options (`CommandTimeout`, `MaxBatchSize`, and `EnableRetryOnFailure` when `MaxRetryCount > 0` and not inside an active non-retrying transaction) + attaches `SlowQueryTelemetryInterceptor`.
* **`TenantConnectionStringNormalizer`:**
  * Adds `NoResetOnClose = pool.NoResetOnClose` and `KeepAlive = pool.KeepAliveSeconds` to `NpgsqlConnectionStringBuilder`.
  * Adds `NormalizeForReadReplica(string connectionString, string? replicaHostOverride)`: if `replicaHostOverride` is provided, rewrites `Host` to the read replica and sets `TargetSessionAttributes = "prefer-standby"`; otherwise returns the normalized primary connection string.

#### 4.3.2. Read-Replica Contexts (`IReadOnlyCatalogDbContext` & `IReadOnlyAppDbContext`)
* **`CatalogReadDbContext` & `TenantReadDbContext` (or read-only query helpers):**
  * Configured with `UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)`.
  * Uses `CatalogReadConnection` (if non-empty, else `CatalogConnection`) and `NormalizeForReadReplica(tenantInfo.ConnectionString, persistenceOptions.TenantReadReplicaHost)` respectively.

#### 4.3.3. Async `TenantWolverineMiddleware` & Wolverine Retry Policies
* **`TenantWolverineMiddleware.BeforeAsync`:**
  * Changed from sync-over-async `store.GetAsync(...).GetAwaiter().GetResult()` to `public async Task BeforeAsync(...)` using `await store.GetAsync(...)` and `await store.GetByIdentifierAsync(...)`.
* **`MultiTenancySetup.UseWolverine`:**
  * Adds transient failure resilience:
    ```csharp
    opts.Policies.OnException<Npgsql.NpgsqlException>(ex => ex.IsTransient)
        .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));
    opts.Policies.OnException<TimeoutException>()
        .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150));
    ```

---

### 4.4. Orleans High-Load Tuning, Polly v8 Resilience & OpenTelemetry

#### 4.4.1. Orleans Silo High-Load Configuration (`ExamPrism.Infrastructure.Orleans`)
* **`OrleansOptions` additions:**
  * `AdvertisedIpAddress` (`string?`), `LoadSheddingEnabled` (`bool`, default `true`), `LoadSheddingLimitPercent` (`int`, default `95`), `CollectionAgeMinutes` (`int`, default `15`), `CollectionQuantumMinutes` (`int`, default `1`), `ResponseTimeoutSeconds` (`int`, default `15`).
* **`OrleansSetup.UseOrleans` additions:**
  * Configures `LoadSheddingOptions` (`LoadSheddingEnabled`, `LoadSheddingLimit`).
  * Configures `GrainCollectionOptions` (`CollectionAge`, `CollectionQuantum`).
  * Configures `SiloMessagingOptions` (`ResponseTimeout`).
  * Calls `siloBuilder.AddActivityPropagation()` to propagate W3C `Activity` context across grain boundaries.
  * When `AdvertisedIpAddress` (or `POD_IP` env var) is valid `IPAddress`, configures `EndpointOptions.AdvertisedIPAddress`.

#### 4.4.2. Polly v8 Resilience Pipelines (`Common.Resilience` & `ExamPrism.Infrastructure.Resilience`)
* **Packages:** `Microsoft.Extensions.Resilience`, `Microsoft.Extensions.Http.Resilience`.
* **`ResiliencePipelineNames` (`Common/Resilience/ResiliencePipelineNames.cs`):**
  * `DefaultIo = "default-io"`.
* **`ResilienceSetup.AddAppResilience`:**
  * Registers `AddResiliencePipeline(ResiliencePipelineNames.DefaultIo, builder => ...)` with Retry (exponential backoff + jitter), Circuit Breaker (`FailureRatio`, `SamplingDuration`, `BreakDuration`), and Timeout (`AttemptTimeoutSeconds`).
  * Configures `services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler())`.

#### 4.4.3. OpenTelemetry Observability Suite (`Common.Telemetry` & `ExamPrism.Infrastructure.Telemetry`)
* **Packages:**
  * `OpenTelemetry.Extensions.Hosting`
  * `OpenTelemetry.Instrumentation.AspNetCore`
  * `OpenTelemetry.Instrumentation.Http`
  * `OpenTelemetry.Instrumentation.Runtime`
  * `OpenTelemetry.Exporter.Prometheus.AspNetCore`
  * `OpenTelemetry.Exporter.OpenTelemetryProtocol`
* **`ExamPrismDiagnostics` (`Common/Telemetry/ExamPrismDiagnostics.cs`):**
  * `ActivitySourceName = "ExamPrism"`, `MeterName = "ExamPrism"`.
  * Static `ActivitySource` and `Meter` with counters:
    * `examprism.db.slow_queries` (`Counter<long>`)
    * `examprism.cache.backplane_evictions` (`Counter<long>`)
    * `examprism.idempotency.replays` (`Counter<long>`)
    * `examprism.idempotency.conflicts` (`Counter<long>`)
* **`TelemetrySetup` (`ExamPrism.Infrastructure.Telemetry`):**
  * Configures OpenTelemetry Tracing (`AddAspNetCoreInstrumentation` filtering out `/health*` and `/metrics`, `AddHttpClientInstrumentation`, `AddSource("ExamPrism", "Microsoft.Orleans.Runtime", "Microsoft.Orleans.Application", "Wolverine", "Npgsql")`).
  * Configures OpenTelemetry Metrics (`AddAspNetCoreInstrumentation`, `AddHttpClientInstrumentation`, `AddRuntimeInstrumentation`, `AddMeter("ExamPrism", "Microsoft.Orleans", "Npgsql", "Microsoft.AspNetCore.RateLimiting")` + `AddPrometheusExporter()`).
  * If `TelemetryOptions.OtlpEndpoint` is non-empty, enables `.UseOtlpExporter()`.
  * Maps `/metrics` via `app.UseOpenTelemetryPrometheusScrapingEndpoint()` when `EnablePrometheusEndpoint == true`, and exempts `/metrics` in `RateLimitingSetup`.

---

## 5. Middleware Pipeline Order (`Program.cs`)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddInfrastructure();

var app = builder.Build();

app.UseAppReverseProxy();          // 1. Resolve real client IP & proto from Load Balancer first
app.UseAppLocalization();          // 2. Request culture
app.UseExceptionHandler();         // 3. Global ProblemDetails exception boundary
app.UseRequestDecompression();     // 4. Decompress incoming gzip/brotli bodies
app.UseResponseCompression();      // 5. Compress outgoing responses
app.UseRouting();                  // 6. Endpoint matching
app.UseRequestTimeouts();          // 7. Enforce per-endpoint / global timeouts
app.UseSerilogRequestLogging(...); // 8. Structured HTTP log
app.UseAppCors();                  // 9. CORS
app.UseMultiTenant();              // 10. Finbuckle tenant resolution
app.UseMiddleware<TenantAccessGuardMiddleware>(); // 11. Active tenant guard
app.UseAuthentication();           // 12. JWT validation
app.UseLogEnrichment();            // 13. Enrich TenantId, UserId, CorrelationId
app.UseRateLimiter();              // 14. Global + Policy + Bulkhead rate limiters
app.UseOutputCache();              // 15. Tenant-scoped output cache
app.UseAuthorization();            // 16. RBAC policies
app.UseAppTelemetry();             // 17. /metrics Prometheus endpoint

if (app.Environment.IsDevelopment())
{
    app.MapAppOpenApi();
}

app.MapEndpoints();
```

---

## 6. Verification and Testing Strategy

1. **Architecture Tests (`ArchitectureTests.cs`):**
   * Verify all new contracts in `Common.*` and implementations in `ExamPrism.Infrastructure.*` pass 100% of existing architecture assertions.
2. **Unit Tests:**
   * `RedisCacheBackplaneTests` & `HybridCacheServiceTests`: Verify L1 invalidation publishing, self-instance loop prevention, and fail-open behavior on Redis error.
   * `DistributedLockTests`: Verify `InMemoryDistributedLock` and `RedisDistributedLock` mutual exclusion, token ownership on release, and startup task single-leader execution.
   * `SlidingWindowRateLimiterTests` & `BulkheadRateLimiterTests`: Verify sliding window sub-segment precision, boundary burst prevention, and concurrency bulkhead queueing/rejection.
   * `IdempotencyEndpointFilterTests`: Verify first-request execution, replay (`X-Idempotency-Replayed: true`), payload hash mismatch (`422`), and concurrent in-flight lock (`409`).
   * `SlowQueryTelemetryInterceptorTests` & `TenantConnectionStringNormalizerTests`: Verify slow query warning + metric increment, `NoResetOnClose`, and read-replica host normalization.
   * `TenantWolverineMiddlewareTests`: Verify true async tenant resolution without thread blocking.
   * `HealthAndDrainTests`: Verify `/health/live` returns `200 OK` even when dependencies fail, and `/health/ready` returns `503` immediately when `HostDrainState.IsDraining` is set.
   * `TelemetryAndResilienceTests`: Verify OpenTelemetry `/metrics` endpoint, `ExamPrismDiagnostics` counters, and Polly v8 `"default-io"` pipeline registration.
