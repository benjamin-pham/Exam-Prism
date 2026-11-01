# Rate Limiting & HybridCache Service with Optional Redis Fallback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a unified `ICacheService` backed by .NET 10 `HybridCache` and an ASP.NET Core Rate Limiting pipeline (`Global` + `"auth"` policies) that automatically leverage Redis (with fail-open resilience and a `"redis"` health check) when `ConnectionStrings:Redis` is configured, and seamlessly fall back to pure in-memory operation when `ConnectionStrings:Redis` is omitted or empty.

**Architecture:** `Common/Caching/ICacheService` and `Common/RateLimiting/RateLimitPolicies` expose transport- and infrastructure-agnostic contracts to vertical slices and endpoints. `ExamPrism.Infrastructure` inspects `configuration.GetConnectionString("Redis")`: when blank, it registers `AddHybridCache` (L1 `IMemoryCache` only) and `InMemoryFixedWindowRateLimiter` (driven by `TimeProvider`); when populated, it registers a shared `IConnectionMultiplexer` (`AbortOnConnectFail = false`), wraps `RedisCache` inside a fail-open `ResilientDistributedCache` (`IDistributedCache` + `IBufferDistributedCache`) for `HybridCache` L2, switches rate-limit partitions to an atomic Lua-script `RedisFixedWindowRateLimiter` with fail-open handling, and adds `RedisHealthCheck` (`"redis"`).

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core 10 Minimal APIs, `Microsoft.Extensions.Caching.Hybrid` 10.0.0, `Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.12, `StackExchange.Redis` 2.8.0, `System.Threading.RateLimiting`, `Microsoft.AspNetCore.RateLimiting`, xUnit, FluentAssertions, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`, `Testcontainers.PostgreSql` 4.16.0, `Testcontainers.Redis` 4.0.0.

**Spec:** `docs/superpowers/specs/2026-11-01-rate-limit-hybrid-cache-design.md`

## Global Constraints

- Target framework `.NET 10` (`net10.0`), C# 13, Central Package Management in `apps/backend/Directory.Packages.props`.
- Exact NuGet package versions (matching local package cache): `Microsoft.Extensions.Caching.Hybrid` `10.0.0`, `Microsoft.Extensions.Caching.StackExchangeRedis` `10.0.12`, `StackExchange.Redis` `2.8.0`, `Testcontainers.Redis` `4.0.0`.
- When `ConnectionStrings:Redis` is `null`, `""`, or whitespace, the application must **not** register `IConnectionMultiplexer`, `IDistributedCache`, or the `"redis"` health check, and must operate 100% in-memory.
- When `ConnectionStrings:Redis` is non-whitespace, `KeyPrefix` (default `"examprism:"`) must be applied via `RedisCacheOptions.InstanceName` for `HybridCache` L2 and prepended to Redis rate-limit keys (`{KeyPrefix}ratelimit:{policy}:{partitionKey}:{bucket}`) without double-prefixing.
- Transient Redis failures (`RedisException`, `TimeoutException`) at runtime must **fail open** (log `Warning` and fall back to L1/factory for caching, or return an acquired `RateLimitLease` for rate limiting) rather than returning HTTP 500, while `RedisHealthCheck` reports `HealthStatus.Unhealthy` so `/health` returns `503`.
- Preserve all `ArchitectureTests` invariants: `ExamPrism.Features..*` has zero `Microsoft.AspNetCore.{Http,Routing,Mvc}` references; `IEndpoint` implementations execute inline inside `MapEndpoint` with zero `Handle`/`HandleAsync` methods and zero `DbContext` references.
- Rate-limit rejections (`429 Too Many Requests`) must emit a positive integer `Retry-After` header and standardized RFC 7807 `ProblemDetails` (`type = "https://tools.ietf.org/html/rfc6585#section-4"`, `title = "Too Many Requests"`, `status = 429`, localized `detail` from `SharedResources["RateLimitExceeded"]` in `vi-VN` / `en-US`, `instance`, and `traceId`).

## Review Focus

1. **L1 cache outliving a short per-call `expiration` override (`expiration < DefaultLocalExpirationMinutes`)**: When `ICacheService.GetOrCreateAsync` or `SetAsync` receives `expiration` shorter than `DefaultLocalExpirationMinutes` without an explicit `localExpiration`, `HybridCacheService` must clamp `LocalCacheExpiration` to `min(expiration, defaultLocalExpiration)` so L1 does not serve stale data after overall expiration (tested in Task 1).
2. **`HybridCache` bypassing `IDistributedCache.GetAsync` via `IBufferDistributedCache`**: Because `DefaultHybridCache` prefers `IBufferDistributedCache.TryGetAsync` / buffer `SetAsync` when the L2 cache implements it (as `RedisCache` does), `ResilientDistributedCache` must implement `IBufferDistributedCache` as well as `IDistributedCache` and catch `RedisException` / `TimeoutException` on both paths so L2 blips truly fail open under `HybridCache` (tested in Task 1).
3. **Window-boundary `Retry-After` rounding to `0` or invalid `WindowSeconds <= 0`**: Both `InMemoryFixedWindowRateLimiter` and `RedisFixedWindowRateLimiter` must guard `permitLimit >= 1` and `window > TimeSpan.Zero`, compute `retryAfter = TimeSpan.FromTicks(Math.Max(1, windowTicks - (nowTicks % windowTicks)))`, and `OnRejected` must write `Retry-After` as `Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))` so clients never receive `Retry-After: 0` (tested in Task 2 and Task 3).
4. **Double quota deduction and two Redis calls when `GlobalLimiter` and `.RequireRateLimiting("auth")` overlap**: `GlobalLimiter` must yield (`RateLimitPartition.GetNoLimiter("endpoint-policy")`) whenever the matched endpoint carries `EnableRateLimitingAttribute` (or `DisableRateLimitingAttribute` / `/health` / `/openapi` / `/scalar`) so `/api/auth/login` runs only the `"auth"` policy once per request (tested in Task 3).
5. **Bare `ServiceCollection` resolution without optional config sections & existing `AuthEndpointTests` hitting `429`**: `AddInfrastructure` must call `services.AddLogging()` (idempotent `TryAdd`) and bind `CacheOptions` / `RateLimitingOptions` with defaults when `"Caching"` / `"RateLimiting"` / `"ConnectionStrings:Redis"` are absent from `IConfiguration`, and `CustomWebApplicationFactory` must set `PermitLimit = 10000` so the 24+ rapid auth requests in `AuthEndpointTests` never fail with spurious `429`s (tested in Task 3 and Task 4).

---

### Task 1: NuGet Packages, `ICacheService`, `HybridCacheService`, `ResilientDistributedCache` & `RedisHealthCheck`

**Files:**
- Modify: `apps/backend/Directory.Packages.props:7-32`
- Modify: `apps/backend/src/ExamPrism.csproj:3-18`
- Modify: `apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj:8-29`
- Create: `apps/backend/src/Common/Caching/ICacheService.cs`
- Create: `apps/backend/src/Infrastructure/Caching/CacheOptions.cs`
- Create: `apps/backend/src/Infrastructure/Caching/HybridCacheService.cs`
- Create: `apps/backend/src/Infrastructure/Caching/ResilientDistributedCache.cs`
- Create: `apps/backend/src/Infrastructure/Caching/RedisHealthCheck.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/HybridCacheServiceTests.cs`

**Interfaces:**
- Consumes: `Microsoft.Extensions.Caching.Hybrid.HybridCache`, `Microsoft.Extensions.Caching.Distributed.IDistributedCache`, `Microsoft.Extensions.Caching.Distributed.IBufferDistributedCache`, `StackExchange.Redis.IConnectionMultiplexer`, `Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck`
- Produces:
  - `Common.Caching.ICacheService` (`GetOrCreateAsync<T>`, `GetOrDefaultAsync<T>`, `SetAsync<T>`, `RemoveAsync`, `RemoveByTagAsync` per Spec §4.1)
  - `ExamPrism.Infrastructure.Caching.CacheOptions` (`SectionName = "Caching"`, `KeyPrefix = "examprism:"`, `DefaultExpirationMinutes = 30`, `DefaultLocalExpirationMinutes = 5`)
  - `ExamPrism.Infrastructure.Caching.HybridCacheService(HybridCache hybridCache, IOptions<CacheOptions> options) : ICacheService`
  - `ExamPrism.Infrastructure.Caching.ResilientDistributedCache(IDistributedCache inner, ILogger<ResilientDistributedCache> logger) : IBufferDistributedCache`
  - `ExamPrism.Infrastructure.Caching.RedisHealthCheck(IConnectionMultiplexer multiplexer) : IHealthCheck`

- [ ] **Step 1: Add NuGet package versions and project references**

- In `apps/backend/Directory.Packages.props`:
  - Under `<!-- API Dependencies -->`, add:
    - `<PackageVersion Include="Microsoft.Extensions.Caching.Hybrid" Version="10.0.0" />`
    - `<PackageVersion Include="Microsoft.Extensions.Caching.StackExchangeRedis" Version="10.0.12" />`
    - `<PackageVersion Include="StackExchange.Redis" Version="2.8.0" />`
  - Under `<!-- Test Dependencies -->`, add:
    - `<PackageVersion Include="Testcontainers.Redis" Version="4.0.0" />`
- In `apps/backend/src/ExamPrism.csproj`, add `<PackageReference>` items for `Microsoft.Extensions.Caching.Hybrid`, `Microsoft.Extensions.Caching.StackExchangeRedis`, and `StackExchange.Redis`.
- In `apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj`, add `<PackageReference Include="Testcontainers.Redis" />`.

- [ ] **Step 2: Write failing unit tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/HybridCacheServiceTests.cs`**

Create `HybridCacheServiceTests` (`namespace Tests.Infrastructure`) testing:
- `GetOrCreateAsync_CachesFactoryValue_AndDoesNotReinvokeFactoryWithinTtl`: builds a `ServiceCollection` (`AddLogging()`, `Configure<CacheOptions>(_ => { })`, `AddHybridCache()`, `AddSingleton<ICacheService, HybridCacheService>()`), calls `GetOrCreateAsync("exam:1", ...)` twice, asserts factory invocation count is `1` and value matches.
- `GetOrDefaultAsync_OnMiss_ReturnsNullWithoutPopulatingCache_AndOnHit_ReturnsCachedValue`: asserts `await cache.GetOrDefaultAsync<SampleDto>("missing")` is `null`, a subsequent `GetOrCreateAsync("missing", ...)` invokes the factory and returns the created DTO, and `GetOrDefaultAsync<SampleDto>("missing")` then returns that DTO.
- `SetAsync_RemoveAsync_And_RemoveByTagAsync_MutateAndInvalidateEntries`: sets two keys sharing tag `"exam-list"` plus one key with tag `"other"`, calls `RemoveByTagAsync("exam-list")`, asserts both `"exam-list"` keys return `null` from `GetOrDefaultAsync` while `"other"` remains cached, then calls `RemoveAsync` on `"other"` and asserts `null`.
- `SetAsync_WithShortExpirationOverride_ClampsLocalExpiration_AndExpiresOnFakeTimeProvider`: registers `FakeTimeProvider` as `TimeProvider` singleton in `ServiceCollection` with `DefaultLocalExpirationMinutes = 5`, calls `SetAsync("short", "val", expiration: TimeSpan.FromSeconds(10))`, advances `FakeTimeProvider` by `11` seconds, and asserts `GetOrDefaultAsync<string>("short")` returns `null` (proving L1 did not stay alive for 5 minutes).
- `ResilientDistributedCache_WhenInnerThrowsRedisExceptionOrTimeout_FailsOpenOnBothStandardAndBufferPaths`: mocks an `IBufferDistributedCache` with `NSubstitute` throwing `new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down")` and `new RedisTimeoutException("timeout", CommandStatus.Unknown)` across `GetAsync`, `TryGetAsync`, `SetAsync` (byte[] and `ReadOnlySequence<byte>`), and `RemoveAsync`; asserts `ResilientDistributedCache` does not throw, returns `null` / `false` on reads, and `HybridCacheService.GetOrCreateAsync` wired over that `ResilientDistributedCache` still executes the factory and returns the value.
- `RedisHealthCheck_ReturnsHealthyOnPing_AndUnhealthyWhenPingThrows`: mocks `IConnectionMultiplexer` + `IDatabase`; when `PingAsync()` returns `TimeSpan.FromMilliseconds(1)` asserts `HealthStatus.Healthy`; when `PingAsync()` throws `RedisConnectionException` asserts `HealthStatus.Unhealthy`.

- [ ] **Step 3: Run `HybridCacheServiceTests` to verify compilation failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~HybridCacheServiceTests"`
Expected: FAIL (`ICacheService`, `CacheOptions`, `HybridCacheService`, `ResilientDistributedCache`, and `RedisHealthCheck` do not exist yet).

- [ ] **Step 4: Implement `ICacheService`, `CacheOptions`, `HybridCacheService`, `ResilientDistributedCache`, and `RedisHealthCheck`**

- Create `apps/backend/src/Common/Caching/ICacheService.cs` with the exact interface in Spec §4.1.
- Create `apps/backend/src/Infrastructure/Caching/CacheOptions.cs` (`public sealed class CacheOptions` with `public const string SectionName = "Caching";`, `KeyPrefix = "examprism:"`, `DefaultExpirationMinutes = 30`, `DefaultLocalExpirationMinutes = 5`).
- Create `apps/backend/src/Infrastructure/Caching/HybridCacheService.cs`:
  - Guard `ArgumentException.ThrowIfNullOrWhiteSpace(key)` (and `tag` / `ArgumentNullException.ThrowIfNull(factory)`).
  - Build `HybridCacheEntryOptions?` via helper `CreateEntryOptions(TimeSpan? expiration, TimeSpan? localExpiration)`: returns `null` when both are `null`; otherwise computes `effectiveExpiration = expiration ?? TimeSpan.FromMinutes(_options.DefaultExpirationMinutes)` and `defaultLocal = TimeSpan.FromMinutes(_options.DefaultLocalExpirationMinutes)`, setting `LocalCacheExpiration = localExpiration ?? (effectiveExpiration < defaultLocal ? effectiveExpiration : defaultLocal)`.
  - Implement `GetOrDefaultAsync<T>` using `_hybridCache.GetOrCreateAsync<object?, T?>(key, state: null, static (_, _) => ValueTask.FromResult<T?>(default), new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableUnderlyingData }, tags: null, cancellationToken)`.
- Create `apps/backend/src/Infrastructure/Caching/ResilientDistributedCache.cs` implementing `IBufferDistributedCache` (and `IDistributedCache`):
  - Forward buffer operations (`bool TryGet(string key, IBufferWriter<byte> destination)`, `ValueTask<bool> TryGetAsync(...)`, `void Set(string key, ReadOnlySequence<byte> value, ...)`, `ValueTask SetAsync(string key, ReadOnlySequence<byte> value, ...)`) to `_inner as IBufferDistributedCache` (or fallback byte-array conversion if `_inner` is plain `IDistributedCache`), wrapping every sync/async method in `try { ... } catch (Exception ex) when (ex is RedisException or TimeoutException)` that logs `_logger.LogWarning(ex, "Redis L2 cache operation failed for key {CacheKey}; failing open.", key)` and returns `null` / `false` / completes cleanly.
- Create `apps/backend/src/Infrastructure/Caching/RedisHealthCheck.cs` implementing `IHealthCheck.CheckHealthAsync`: awaits `multiplexer.GetDatabase().PingAsync()` and returns `HealthCheckResult.Healthy()`, catching `Exception ex` (except `OperationCanceledException` when `cancellationToken.IsCancellationRequested`) and returning `HealthCheckResult.Unhealthy("Redis connection failed.", ex)`.

- [ ] **Step 5: Run `HybridCacheServiceTests` to verify all tests pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~HybridCacheServiceTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add apps/backend/Directory.Packages.props apps/backend/src/ExamPrism.csproj apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj apps/backend/src/Common/Caching/ICacheService.cs apps/backend/src/Infrastructure/Caching apps/backend/tests/ExamPrism.Tests/Infrastructure/HybridCacheServiceTests.cs
git commit -m "feat(caching): add ICacheService, HybridCacheService, fail-open ResilientDistributedCache, and RedisHealthCheck"
```

---

### Task 2: `RateLimitPolicies`, `RateLimitingOptions`, `InMemoryFixedWindowRateLimiter` & `RedisFixedWindowRateLimiter`

**Files:**
- Create: `apps/backend/src/Common/RateLimiting/RateLimitPolicies.cs`
- Create: `apps/backend/src/Infrastructure/RateLimiting/RateLimitingOptions.cs`
- Create: `apps/backend/src/Infrastructure/RateLimiting/InMemoryFixedWindowRateLimiter.cs`
- Create: `apps/backend/src/Infrastructure/RateLimiting/RedisFixedWindowRateLimiter.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/RateLimiterUnitTests.cs`

**Interfaces:**
- Consumes: `System.Threading.RateLimiting.RateLimiter`, `System.Threading.RateLimiting.RateLimitLease`, `System.Threading.RateLimiting.MetadataName`, `StackExchange.Redis.IConnectionMultiplexer`
- Produces:
  - `Common.RateLimiting.RateLimitPolicies` (`public const string Auth = "auth";`)
  - `ExamPrism.Infrastructure.RateLimiting.FixedWindowPolicyOptions` (`int PermitLimit`, `int WindowSeconds`)
  - `ExamPrism.Infrastructure.RateLimiting.RateLimitingOptions` (`SectionName = "RateLimiting"`, `FixedWindowPolicyOptions Global` `[100, 60]`, `FixedWindowPolicyOptions Auth` `[10, 60]`)
  - `ExamPrism.Infrastructure.RateLimiting.InMemoryFixedWindowRateLimiter(int permitLimit, TimeSpan window, TimeProvider timeProvider) : RateLimiter`
  - `ExamPrism.Infrastructure.RateLimiting.RedisFixedWindowRateLimiter(IConnectionMultiplexer multiplexer, string redisKeyPrefix, int permitLimit, TimeSpan window, TimeProvider timeProvider, ILogger logger) : RateLimiter`

- [ ] **Step 1: Write failing unit tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/RateLimiterUnitTests.cs`**

Create `RateLimiterUnitTests` (`namespace Tests.Infrastructure`) testing:
- `InMemoryFixedWindowRateLimiter_AcquiresUpToPermitLimit_RejectsWithPositiveRetryAfter_AndResetsOnWindowAdvance`: uses `FakeTimeProvider` at `2026-11-01T12:00:15Z` with `permitLimit: 2`, `window: TimeSpan.FromSeconds(60)`; asserts first 2 `AcquireAsync(1)` return `IsAcquired == true`, 3rd returns `IsAcquired == false` with `TryGetMetadata(MetadataName.RetryAfter, out var retry)` == `true` and `retry == TimeSpan.FromSeconds(45)`; advances `FakeTimeProvider` by `45` seconds and asserts next `AcquireAsync(1)` returns `IsAcquired == true`.
- `InMemoryFixedWindowRateLimiter_AtLastTickOfWindow_StillReturnsPositiveRetryAfter_AndValidatesConstructorArgs`: sets `FakeTimeProvider` 1 tick before window boundary, exceeds limit, asserts `RetryAfter == TimeSpan.FromTicks(1)` (`> TimeSpan.Zero`); asserts `permitLimit <= 0` or `window <= TimeSpan.Zero` throws `ArgumentOutOfRangeException`.
- `RedisFixedWindowRateLimiter_EvaluatesAtomicLuaScript_EnforcesPermitLimit_AndResetsBucketOnTimeAdvance`: mocks `IConnectionMultiplexer` + `IDatabase.ScriptEvaluateAsync(...)` with `NSubstitute` returning `RedisResult.Create(1L)`, `RedisResult.Create(2L)`, then `RedisResult.Create(3L)` for `permitLimit: 2`, `window: TimeSpan.FromSeconds(60)` and `redisKeyPrefix: "examprism:ratelimit:auth:ip:127.0.0.1"`; verifies `KEYS[0]` includes the computed window bucket, `ARGV[1] == 60000`, 3rd call returns `IsAcquired == false` with `MetadataName.RetryAfter`, and advancing `FakeTimeProvider` by 60 seconds changes `KEYS[0]` to the next bucket.
- `RedisFixedWindowRateLimiter_WhenRedisThrowsConnectionOrTimeoutException_FailsOpenAndAcquiresLease`: configures `IDatabase.ScriptEvaluateAsync` to throw `new RedisConnectionException(ConnectionFailureType.SocketFailure, "lost")` and `new RedisTimeoutException("timeout", CommandStatus.Unknown)`; asserts `AcquireAsync(1)` does not throw and returns `lease.IsAcquired == true`.

- [ ] **Step 2: Run `RateLimiterUnitTests` to verify compilation failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~RateLimiterUnitTests"`
Expected: FAIL (`InMemoryFixedWindowRateLimiter` and `RedisFixedWindowRateLimiter` do not exist yet).

- [ ] **Step 3: Implement `RateLimitPolicies`, `RateLimitingOptions`, `InMemoryFixedWindowRateLimiter`, and `RedisFixedWindowRateLimiter`**

- Create `apps/backend/src/Common/RateLimiting/RateLimitPolicies.cs` and `apps/backend/src/Infrastructure/RateLimiting/RateLimitingOptions.cs` per Spec §3.2 & §5.1.
- Implement `apps/backend/src/Infrastructure/RateLimiting/InMemoryFixedWindowRateLimiter.cs`:
  - Validate `ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1)`, `ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero)`, `ArgumentNullException.ThrowIfNull(timeProvider)`.
  - Guard `long _windowBucket = -1; int _count = 0;` with `private readonly Lock _lock = new();`.
  - In `AttemptAcquireCore(int permitCount)` (and `AcquireAsyncCore` delegating to `ValueTask.FromResult(AttemptAcquireCore(permitCount))`): handle `permitCount == 0` (`isAcquired: _count < _permitLimit`), compute `nowTicks = _timeProvider.GetUtcNow().UtcTicks`, `bucket = nowTicks / _window.Ticks`, `retryAfter = TimeSpan.FromTicks(Math.Max(1, _window.Ticks - (nowTicks % _window.Ticks)))`; under `_lock`, reset `_windowBucket = bucket; _count = 0;` if `bucket != _windowBucket`, increment if `_count + permitCount <= _permitLimit` -> return acquired lease, else return rejected lease carrying `MetadataName.RetryAfter = retryAfter`.
- Implement `apps/backend/src/Infrastructure/RateLimiting/RedisFixedWindowRateLimiter.cs`:
  - Validate constructor arguments (`permitLimit >= 1`, `window > TimeSpan.Zero`, non-null `multiplexer`, `redisKeyPrefix`, `timeProvider`, `logger`).
  - Use the exact atomic Lua script in Spec §5.3 (`INCRBY` + conditional `PEXPIRE` when `current == tonumber(ARGV[1]) or redis.call('PTTL', KEYS[1]) < 0`).
  - In `AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)`: compute `bucket` and `retryAfter` from `_timeProvider.GetUtcNow().UtcTicks`, key `$"{_redisKeyPrefix}:{bucket}"`, `windowMs = Math.Max(1L, (long)Math.Ceiling(_window.TotalMilliseconds))`, evaluate script via `_multiplexer.GetDatabase().ScriptEvaluateAsync(...)`, return acquired lease when `(long)result <= _permitLimit` else rejected lease with `MetadataName.RetryAfter = retryAfter`; catch `(Exception ex) when (ex is RedisException or TimeoutException)` -> `_logger.LogWarning(ex, "Redis rate limiter failed for key {RateLimitKey}; failing open.", key)` and return acquired lease. (Implement `AttemptAcquireCore` symmetrically via synchronous `ScriptEvaluate` with the same `try`/`catch` fail-open guard.)

- [ ] **Step 4: Run `RateLimiterUnitTests` to verify all tests pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~RateLimiterUnitTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/RateLimiting/RateLimitPolicies.cs apps/backend/src/Infrastructure/RateLimiting apps/backend/tests/ExamPrism.Tests/Infrastructure/RateLimiterUnitTests.cs
git commit -m "feat(rate-limiting): add TimeProvider-driven in-memory and fail-open Redis Lua fixed-window rate limiters"
```

---

### Task 3: Conditional Redis/In-Memory DI Wiring, Middleware Pipeline, Endpoint Attributes & In-Memory HTTP Integration Tests

**Files:**
- Modify: `apps/backend/src/Common/Localization/Resources/SharedResources.resx:61-73`
- Modify: `apps/backend/src/Common/Localization/Resources/SharedResources.vi.resx:61-73`
- Modify: `apps/backend/src/appsettings.json:9-19`
- Create: `apps/backend/src/Infrastructure/RateLimiting/RateLimitingSetup.cs`
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs:15-93`
- Modify: `apps/backend/src/Program.cs:69-73`
- Modify: `apps/backend/src/Endpoints/Health/GetHealthEndpoint.cs:17-31`
- Modify: `apps/backend/src/Endpoints/Auth/LoginEndpoint.cs:17-41`
- Modify: `apps/backend/src/Endpoints/Auth/RegisterEndpoint.cs:17-41`
- Modify: `apps/backend/src/Endpoints/Auth/RefreshEndpoint.cs:17-49`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs:18-21`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs:164-179`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/RateLimitingIntegrationTests.cs`

**Interfaces:**
- Consumes: `Common.Caching.ICacheService`, `ExamPrism.Infrastructure.Caching.*`, `Common.RateLimiting.RateLimitPolicies`, `ExamPrism.Infrastructure.RateLimiting.*`, `Common.Localization.SharedResources`
- Produces:
  - `ExamPrism.Infrastructure.RateLimiting.RateLimitingSetup.AddExamPrismRateLimiting(this IServiceCollection services, IConfiguration configuration, bool useRedis)`
  - Updated `DependencyInjection.AddInfrastructure(this IServiceCollection services, IConfiguration configuration)` registering conditional Redis vs In-Memory Cache, Rate Limiting, and HealthChecks
  - Updated `Program.cs` pipeline (`app.UseRateLimiter()` between `app.UseAuthentication()` and `app.UseAuthorization()`)
  - Rate-limit metadata on `GetHealthEndpoint` (`.DisableRateLimiting()`) and `LoginEndpoint` / `RegisterEndpoint` / `RefreshEndpoint` (`.RequireRateLimiting(RateLimitPolicies.Auth)`)

- [ ] **Step 1: Write failing DI and HTTP integration tests in `DependencyInjectionTests.cs` and `RateLimitingIntegrationTests.cs`**

- In `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`, add:
  - `AddInfrastructure_WhenRedisConnectionStringOmittedOrWhitespace_RegistersInMemoryHybridCacheOnly_WithoutRedisOrRedisHealthCheck` (`Theory` over `null`, `""`, `"   "`): asserts `provider.GetService<Common.Caching.ICacheService>()` is `HybridCacheService`, `provider.GetService<IConnectionMultiplexer>()` is `null`, `provider.GetService<IDistributedCache>()` is `null`, and `IOptions<HealthCheckServiceOptions>().Value.Registrations` contains `"database"` and does **not** contain `"redis"`.
  - `AddInfrastructure_WhenRedisConnectionStringProvided_RegistersMultiplexerResilientDistributedCacheAndRedisHealthCheck`: passes `"ConnectionStrings:Redis" = "localhost:6399"` (non-running port — proves `AbortOnConnectFail = false` does not crash startup/DI resolution!), asserts `services` contains `IConnectionMultiplexer`, `IDistributedCache` resolves to `ResilientDistributedCache`, `ICacheService` resolves to `HybridCacheService`, and `HealthCheckServiceOptions.Registrations` contains both `"database"` and `"redis"`.
- Create `apps/backend/tests/ExamPrism.Tests/Infrastructure/RateLimitingIntegrationTests.cs` (`IClassFixture<CustomWebApplicationFactory>`) using `_factory.WithWebHostBuilder(...)` overriding `RateLimiting:Auth:PermitLimit = 2`, `RateLimiting:Auth:WindowSeconds = 60`, `RateLimiting:Global:PermitLimit = 3`, `RateLimiting:Global:WindowSeconds = 60`:
  - `AuthEndpoints_ExceedingAuthPermitLimit_Returns429ProblemDetailsWithRetryAfter_LocalizedInViAndEn`: posts 2 invalid logins to `/api/auth/login` (`401`), then posts a 3rd with `Accept-Language: vi-VN` -> asserts `429 Too Many Requests`, `Content-Type` `application/problem+json`, `Retry-After` header integer `>= 1`, JSON `status == 429`, `title == "Too Many Requests"`, `detail == "Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau."`, non-empty `traceId`; posts a 4th with `Accept-Language: en-US` -> asserts `detail == "Too many requests. Please try again later."`.
  - `GlobalLimiter_PartitionsByAuthenticatedUserId_SeparatelyFromOtherUsersAndAnonymousIp`: registers User A and User B, calls `GET /api/auth/me` 3 times as User A (`200 OK`) and a 4th time as User A (`429`), then immediately calls `GET /api/auth/me` as User B -> asserts `200 OK` (proving `user:{userA}` did not exhaust `user:{userB}`'s quota, and `Register` under `"auth"` policy did not deduct from Global quota).
  - `HealthEndpoint_IsNeverRateLimited_EvenAfterAuthAndGlobalQuotasAreExhausted`: calls `GET /health` 10 times after `429` has already triggered on `/api/auth/login` and `/api/auth/me` -> asserts every `/health` response is `200 OK`.

- [ ] **Step 2: Run the new DI and integration tests to verify they fail**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~AddInfrastructure_WhenRedis|FullyQualifiedName~RateLimitingIntegrationTests"`
Expected: FAIL (`ICacheService` not registered in `AddInfrastructure` and rate limiting not wired).

- [ ] **Step 3: Implement localization keys, `appsettings.json`, `RateLimitingSetup`, `DependencyInjection`, `Program.cs`, endpoint attributes, and `CustomWebApplicationFactory` test limits**

- Add `<data name="RateLimitExceeded" xml:space="preserve">` to `SharedResources.resx` (`"Too many requests. Please try again later."`) and `SharedResources.vi.resx` (`"Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau."`).
- Update `apps/backend/src/appsettings.json` with `"Redis": ""` under `"ConnectionStrings"`, plus `"Caching"` and `"RateLimiting"` sections matching Spec §3.
- Create `apps/backend/src/Infrastructure/RateLimiting/RateLimitingSetup.cs`:
  - Bind `RateLimitingOptions` and `CacheOptions` from `IConfiguration` (falling back to `new RateLimitingOptions()` / `new CacheOptions()` when sections are omitted).
  - Configure `services.AddRateLimiter(options => ...)`:
    - `RejectionStatusCode = StatusCodes.Status429TooManyRequests`.
    - `OnRejected`: sets `ContentType = "application/problem+json"`, writes `Retry-After` header `Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture)` when `MetadataName.RetryAfter` is present, resolves `IStringLocalizer<SharedResources>` for `"RateLimitExceeded"`, and writes RFC 7807 `ProblemDetails` (`type = "https://tools.ietf.org/html/rfc6585#section-4"`, `title = "Too Many Requests"`, `status = 429`, `detail`, `instance = path`, `Extensions["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier`).
    - Policy `RateLimitPolicies.Auth`: partitions `HttpContext` by `$"auth:ip:{GetClientIp(httpContext)}"`, creating `RedisFixedWindowRateLimiter` when `useRedis` is `true` (key prefix `$"{cacheOptions.KeyPrefix}ratelimit:auth:ip:{ip}"`) or `InMemoryFixedWindowRateLimiter` when `false`, resolving `TimeProvider` (and `IConnectionMultiplexer` / `ILoggerFactory` in Redis mode) from `httpContext.RequestServices` (cached inside the partition factory closure or resolved once via `AddOptions<RateLimiterOptions>().Configure<IServiceProvider>(...)` so partition limiters persist across requests!).
      - **Important implementation note**: Register via `services.AddRateLimiter(); services.AddOptions<RateLimiterOptions>().Configure<IServiceProvider>((options, sp) => ...)` so `PartitionedRateLimiter.Create<HttpContext, string>` and `options.AddPolicy<string>(RateLimitPolicies.Auth, ...)` capture singleton `TimeProvider`, `ILoggerFactory`, and (when `useRedis`) `IConnectionMultiplexer` from the root `IServiceProvider`, allowing ASP.NET Core's `DefaultPartitionedRateLimiter` to cache and reuse each partition's `RateLimiter` instance across HTTP requests!
    - `GlobalLimiter`: returns `RateLimitPartition.GetNoLimiter("infra")` if endpoint has `DisableRateLimitingAttribute` or path equals/starts with `/health`, `/openapi`, or `/scalar` (`OrdinalIgnoreCase`); returns `RateLimitPartition.GetNoLimiter("endpoint-policy")` if endpoint has `EnableRateLimitingAttribute`; otherwise partitions by `$"global:user:{userId}"` (when authenticated with non-empty `ClaimTypes.NameIdentifier` or `"sub"`) or `$"global:ip:{GetClientIp(httpContext)}"` using `RateLimitingOptions.Global`.
- Update `apps/backend/src/Infrastructure/DependencyInjection.cs`:
  - Call `services.AddLogging();` (idempotent `TryAdd` for bare `ServiceCollection` tests).
  - Bind `services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName))` and `services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName))`.
  - Check `var redisConnectionString = configuration.GetConnectionString("Redis"); var useRedis = !string.IsNullOrWhiteSpace(redisConnectionString);`.
  - If `useRedis`:
    - Register singleton `IConnectionMultiplexer` via `ConfigurationOptions.Parse(redisConnectionString!)` with `AbortOnConnectFail = false` (`ConnectionMultiplexer.Connect(redisOptions)`).
    - Register singleton `IDistributedCache` constructing `new RedisCache(Options.Create(new RedisCacheOptions { InstanceName = cacheOptions.KeyPrefix, ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>()) }))` wrapped in `new ResilientDistributedCache(redisCache, sp.GetRequiredService<ILogger<ResilientDistributedCache>>())`.
    - Chain `.AddCheck<RedisHealthCheck>("redis")` onto `services.AddHealthChecks()`.
  - Register `services.AddHybridCache(options => { options.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(cacheOptions.DefaultExpirationMinutes), LocalCacheExpiration = TimeSpan.FromMinutes(cacheOptions.DefaultLocalExpirationMinutes) }; });` and `services.AddSingleton<Common.Caching.ICacheService, HybridCacheService>();`.
  - Call `services.AddExamPrismRateLimiting(configuration, useRedis);`.
- In `apps/backend/src/Program.cs`, insert `app.UseRateLimiter();` right after `app.UseAuthentication();` and before `app.UseAuthorization();`.
- Attach `.DisableRateLimiting()` in `GetHealthEndpoint.cs`, and `.RequireRateLimiting(RateLimitPolicies.Auth)` in `LoginEndpoint.cs`, `RegisterEndpoint.cs`, and `RefreshEndpoint.cs`.
- In `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs`, add `builder.UseSetting("RateLimiting:Global:PermitLimit", "10000");` and `builder.UseSetting("RateLimiting:Auth:PermitLimit", "10000");` inside `ConfigureWebHost` so existing multi-request functional suites like `AuthEndpointTests` remain unthrottled by default.

- [ ] **Step 4: Run DI tests, `RateLimitingIntegrationTests`, and `AuthEndpointTests` to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~DependencyInjectionTests|FullyQualifiedName~RateLimitingIntegrationTests|FullyQualifiedName~AuthEndpointTests|FullyQualifiedName~HealthEndpointTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Localization/Resources apps/backend/src/appsettings.json apps/backend/src/Infrastructure apps/backend/src/Program.cs apps/backend/src/Endpoints apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/RateLimitingIntegrationTests.cs
git commit -m "feat(infrastructure): wire conditional Redis/in-memory HybridCache, rate limiting middleware, and localized 429 responses"
```

---

### Task 4: `Testcontainers.Redis` End-to-End Integration Tests & Full Architecture Verification

**Files:**
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/RedisIntegrationTests.cs`

**Interfaces:**
- Consumes: `Testcontainers.PostgreSql.PostgreSqlContainer`, `Testcontainers.Redis.RedisContainer`, `Common.Caching.ICacheService`, `StackExchange.Redis.IConnectionMultiplexer`
- Produces:
  - `Tests.Infrastructure.RedisWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime` (boots `postgres:17-alpine` + `redis:7-alpine`, configures `ConnectionStrings:DefaultConnection`, `ConnectionStrings:Redis`, `RateLimiting:Auth:PermitLimit = 2`, `RateLimiting:Global:PermitLimit = 3`)
  - `Tests.Infrastructure.RedisIntegrationTests` verifying L2 Redis persistence, distributed Redis rate limiting, `/health` Redis monitoring, and live runtime fail-open when Redis disconnects

- [ ] **Step 1: Write `RedisIntegrationTests` in `apps/backend/tests/ExamPrism.Tests/Infrastructure/RedisIntegrationTests.cs`**

Create `RedisWebApplicationFactory` and `RedisIntegrationTests` (`IClassFixture<RedisWebApplicationFactory>`) testing:
- `CacheService_WithRedisConfigured_PersistsEntriesToRedisL2WithConfiguredKeyPrefix`: resolves `ICacheService` and `IConnectionMultiplexer`, calls `await cache.SetAsync("quiz:42", new QuizCacheProbe("Math", 50))`, asserts `await multiplexer.GetDatabase().KeyExistsAsync("examprism:quiz:42")` is `true`, and `await cache.GetOrDefaultAsync<QuizCacheProbe>("quiz:42")` returns the expected record.
- `RateLimiting_WithRedisConfigured_StoresCountersInRedis_AndReturns429WhenPermitLimitExceeded`: sends 2 `POST /api/auth/login` requests (`401`) and a 3rd (`429 Too Many Requests` with `Retry-After` header), and asserts via `IConnectionMultiplexer.GetServer(...).Keys(pattern: "examprism:ratelimit:auth:*")` that the Redis rate-limit key was created in Redis.
- `HealthEndpoint_WithRedisConfigured_Returns200HealthyWhenRedisUp`: calls `GET /health` and asserts `200 OK` with `Status == "Healthy"`.
- `UnreachableRedis_ReportsUnhealthy503OnHealthEndpoint_WhileApiEndpointsFailOpenWithout500`: uses `WithWebHostBuilder` overriding `ConnectionStrings:Redis` to an unreachable local port (`"127.0.0.1:6399,connectTimeout=200,syncTimeout=200"`), asserts `GET /health` returns `503 ServiceUnavailable` with `Status == "Unhealthy"`, while `POST /api/auth/login` with wrong credentials still fails open through the Redis rate limiter and returns `401 Unauthorized` (never `500`), and `ICacheService.GetOrCreateAsync` still executes its factory and returns the value.

- [ ] **Step 2: Run `RedisIntegrationTests` to verify real Redis L2 + Redis Lua rate-limiting + `/health` pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~RedisIntegrationTests"`
Expected: PASS.

- [ ] **Step 3: Run `ArchitectureTests` and the entire test solution**

Run: `dotnet test apps/backend/ExamPrism.slnx`
Expected: PASS across all unit, architecture, in-memory integration, and Redis Testcontainers integration tests with 0 warnings and 0 errors.

- [ ] **Step 4: Commit**

```bash
git add apps/backend/tests/ExamPrism.Tests/Infrastructure/RedisIntegrationTests.cs
git commit -m "test(redis): add Testcontainers.Redis integration tests for HybridCache L2, distributed rate limiting, and health check"
```
