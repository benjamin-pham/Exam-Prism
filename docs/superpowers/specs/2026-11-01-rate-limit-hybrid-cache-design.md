# Design Specification: Rate Limiting and HybridCache Service with Optional Redis Fallback

**Date:** 2026-11-01  
**Status:** Draft (Pending User Review)  
**Scope:** Architectural Subsystem  

---

## 1. Context and Goals

ExamPrism backend currently operates on ASP.NET Core 10 Minimal APIs (`IEndpoint` auto-registration, Wolverine vertical-slice handlers, PostgreSQL + EF Core, and JWT authentication) without application caching or API rate limiting.

This specification defines a unified caching and rate-limiting architecture that supports **two seamless runtime modes** controlled solely by whether `ConnectionStrings:Redis` is configured:

1. **In-Memory Mode (Default when `ConnectionStrings:Redis` is absent, null, or whitespace)**:
   * **Cache Service**: Runs .NET `HybridCache` purely on L1 (`IMemoryCache`) with stampede protection and tag-based invalidation (`RemoveByTagAsync`).
   * **Rate Limiting**: Runs `InMemoryFixedWindowRateLimiter` (`System.Threading.RateLimiting.RateLimiter`) per process driven by `TimeProvider`.
   * **Health Checks**: Does **not** register a Redis health check; `/health` checks only `"database"`.
2. **Hybrid / Distributed Redis Mode (When `ConnectionStrings:Redis` is provided)**:
   * **Shared Connection**: Registers a singleton `IConnectionMultiplexer` (`AbortOnConnectFail = false`).
   * **Cache Service**: Runs `HybridCache` with L1 (`IMemoryCache`) + L2 (`RedisCache` wrapped in `ResilientDistributedCache` for fail-open resilience).
   * **Rate Limiting**: Distributes rate-limit counters across instances via an atomic Lua-script `RedisFixedWindowRateLimiter` backed by `IConnectionMultiplexer` and `TimeProvider`, with fail-open behavior on transient Redis errors.
   * **Health Checks**: Automatically registers a `"redis"` health check (`PING`) so `/health` monitors both `"database"` and `"redis"`.

---

## 2. Architecture & Tech Stack

* **Platform:** .NET 10 Minimal APIs (`net10.0`) with `IEndpoint` and Wolverine `IHandler` architecture.
* **Caching:** `Microsoft.Extensions.Caching.Hybrid` (`HybridCache`), `Microsoft.Extensions.Caching.StackExchangeRedis` (`RedisCache`), and `StackExchange.Redis` (`IConnectionMultiplexer`).
* **Rate Limiting:** Built-in `Microsoft.AspNetCore.RateLimiting` and `System.Threading.RateLimiting` (`PartitionedRateLimiter<HttpContext>`, `RateLimiter`).
* **Time Abstraction:** Standard .NET `TimeProvider` (singleton in DI, deterministic in tests via `FakeTimeProvider`).
* **Localization & Errors:** Standardized RFC 7807 `ProblemDetails` (`429 Too Many Requests`) localized via `IStringLocalizer<SharedResources>` (`vi-VN` / `en-US`).
* **Testing:** xUnit, FluentAssertions, NSubstitute, `Microsoft.Extensions.TimeProvider.Testing`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`, and `Testcontainers.Redis`.

---

## 3. Configuration Schema

Configured in `appsettings.json` (and overridable via environment variables):

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=examprism_dev;Username=postgres;Password=postgres",
    "Redis": ""
  },
  "Caching": {
    "KeyPrefix": "examprism:",
    "DefaultExpirationMinutes": 30,
    "DefaultLocalExpirationMinutes": 5
  },
  "RateLimiting": {
    "Global": {
      "PermitLimit": 100,
      "WindowSeconds": 60
    },
    "Auth": {
      "PermitLimit": 10,
      "WindowSeconds": 60
    }
  }
}
```

### 3.1. `CacheOptions` (`ExamPrism.Infrastructure.Caching`)
* `SectionName = "Caching"`
* `KeyPrefix` (`string`, default `"examprism:"`): Applied as `RedisCacheOptions.InstanceName` for L2 cache keys and prepended to Redis rate-limit keys so all Redis entries share a single namespace without double-prefixing.
* `DefaultExpirationMinutes` (`int`, default `30`): Default overall / L2 TTL for `HybridCache`.
* `DefaultLocalExpirationMinutes` (`int`, default `5`): Default L1 in-memory TTL for `HybridCache`.

### 3.2. `RateLimitingOptions` (`ExamPrism.Infrastructure.RateLimiting`)
* `SectionName = "RateLimiting"`
* `Global` (`FixedWindowPolicyOptions`): Default `PermitLimit = 100`, `WindowSeconds = 60`.
* `Auth` (`FixedWindowPolicyOptions`): Default `PermitLimit = 10`, `WindowSeconds = 60`.

---

## 4. Cache Service Design

### 4.1. `ICacheService` Abstraction (`Common.Caching`)
Placed in `Common/Caching/ICacheService.cs` so Wolverine handlers in `ExamPrism.Features` remain decoupled from infrastructure packages and satisfy `ArchitectureTests`:

```csharp
namespace Common.Caching;

public interface ICacheService
{
    ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default);

    ValueTask<T?> GetOrDefaultAsync<T>(
        string key,
        CancellationToken cancellationToken = default);

    ValueTask SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(
        string key,
        CancellationToken cancellationToken = default);

    ValueTask RemoveByTagAsync(
        string tag,
        CancellationToken cancellationToken = default);
}
```

### 4.2. `HybridCacheService` Implementation (`ExamPrism.Infrastructure.Caching`)
* Wraps `Microsoft.Extensions.Caching.Hybrid.HybridCache` and `IOptions<CacheOptions>`.
* Validates `ArgumentException.ThrowIfNullOrWhiteSpace(key)` (and `tag` in `RemoveByTagAsync`, `ArgumentNullException.ThrowIfNull(factory)` in `GetOrCreateAsync`).
* **`GetOrCreateAsync<T>`**: Delegates to `HybridCache.GetOrCreateAsync`, building `HybridCacheEntryOptions` only when `expiration` or `localExpiration` overrides are supplied:
  * If `expiration` is provided without `localExpiration`, `LocalCacheExpiration` is clamped to `min(expiration, DefaultLocalExpiration)` so L1 never outlives L2/overall expiration.
* **`GetOrDefaultAsync<T>`**: Calls `HybridCache.GetOrCreateAsync` with `HybridCacheEntryFlags.DisableUnderlyingData` so a cache miss neither invokes the factory nor writes a default/null entry to L1 or L2, returning `default(T?)`.
* **`SetAsync<T>`**, **`RemoveAsync`**, **`RemoveByTagAsync`**: Delegate directly to `HybridCache.SetAsync`, `HybridCache.RemoveAsync`, and `HybridCache.RemoveByTagAsync`.

### 4.3. `ResilientDistributedCache` Fail-Open L2 Decorator (`ExamPrism.Infrastructure.Caching`)
When `ConnectionStrings:Redis` is configured:
* Instantiates `RedisCache` (`Microsoft.Extensions.Caching.StackExchangeRedis`) configured with:
  * `InstanceName = cacheOptions.KeyPrefix`
  * `ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer)` (reusing the shared singleton `IConnectionMultiplexer`).
* Wraps `RedisCache` inside `ResilientDistributedCache : IDistributedCache` (also implementing `IBufferDistributedCache` to preserve `HybridCache`'s buffer-based serialization path).
* Every L2 operation (`Get`, `GetAsync`, `TryGetAsync`, `Set`, `SetAsync`, `Refresh`, `RefreshAsync`, `Remove`, `RemoveAsync`) catches `RedisException` and `TimeoutException`:
  * Logs a structured `LogWarning` with the cache key and exception.
  * On reads: returns `null` / `false` (treating L2 failure as an L2 cache miss so `HybridCache` transparently executes the factory and serves from L1).
  * On writes/removals: swallows the L2 failure after logging so the request succeeds without throwing HTTP 500.

---

## 5. Rate Limiting Design

### 5.1. Policy Constants (`Common.RateLimiting`)
```csharp
namespace Common.RateLimiting;

public static class RateLimitPolicies
{
    public const string Auth = "auth";
}
```

### 5.2. Partitioning & Endpoint Rules
1. **Infrastructure Endpoints Bypass (`NoLimiter`)**:
   * `GetHealthEndpoint` (`/health`) explicitly declares `.DisableRateLimiting()`.
   * As defense-in-depth, `GlobalLimiter` also returns `RateLimitPartition.GetNoLimiter("infra")` when the request path matches `/health`, `/openapi` (or `/openapi/*`), or `/scalar` (or `/scalar/*`) case-insensitively, or when `DisableRateLimitingAttribute` metadata is present.
2. **Named Policy `"auth"` (`RateLimitPolicies.Auth`)**:
   * Applied via `.RequireRateLimiting(RateLimitPolicies.Auth)` on:
     * `POST /api/auth/login` (`LoginEndpoint`)
     * `POST /api/auth/register` (`RegisterEndpoint`)
     * `POST /api/auth/refresh` (`RefreshEndpoint`)
   * Partition key: **`ip:{clientIp}`** (where `clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"`).
   * Limits: `RateLimitingOptions.Auth` (`PermitLimit = 10`, `WindowSeconds = 60` by default).
3. **Global Default Limiter (`RateLimiterOptions.GlobalLimiter`)**:
   * If the matched endpoint carries `EnableRateLimitingAttribute` (such as `"auth"`), `GlobalLimiter` returns `RateLimitPartition.GetNoLimiter("endpoint-policy")` so the named policy governs quota exclusively (preventing double-counting and avoiding two Redis round-trips per request).
   * For all other endpoints:
     * If `httpContext.User.Identity?.IsAuthenticated == true` and claim `ClaimTypes.NameIdentifier` (or `"sub"`) is non-empty: partition key is **`user:{userId}`**.
     * Otherwise (anonymous request): partition key is **`ip:{clientIp}`**.
   * Limits: `RateLimitingOptions.Global` (`PermitLimit = 100`, `WindowSeconds = 60` by default).

### 5.3. Dual-Backend Limiter Creation (Redis vs. In-Memory)
* **In-Memory Mode (`ConnectionStrings:Redis` absent or whitespace)**:
  * Partition factory creates `InMemoryFixedWindowRateLimiter : RateLimiter` per partition key, driven directly by the DI-registered `TimeProvider`:
    * Tracks the active window bucket (`long _windowBucket = nowTicks / windowTicks`) and request counter (`int _count`) under a lightweight synchronization lock.
    * Resets `_count` automatically whenever `timeProvider.GetUtcNow().UtcTicks / window.Ticks` advances to a new bucket, requiring zero background timers and enabling deterministic window-reset verification via `FakeTimeProvider`.
    * When `_count > permitLimit`, returns a rejected `RateLimitLease` exposing `MetadataName.RetryAfter` equal to the remaining duration of the current window.
* **Redis Mode (`ConnectionStrings:Redis` provided)**:
  * Partition factory creates `RedisFixedWindowRateLimiter : RateLimiter`:
    * Computes `nowTicks = timeProvider.GetUtcNow().UtcTicks`, `windowTicks = window.Ticks`, `bucket = nowTicks / windowTicks`, and `retryAfter = TimeSpan.FromTicks(windowTicks - (nowTicks % windowTicks))`.
    * Redis key: `{KeyPrefix}ratelimit:{policyName}:{partitionKey}:{bucket}`.
    * Executes atomic Lua script on `IConnectionMultiplexer.GetDatabase()`:
      ```lua
      local current = redis.call('INCRBY', KEYS[1], ARGV[1])
      if current == tonumber(ARGV[1]) or redis.call('PTTL', KEYS[1]) < 0 then
          redis.call('PEXPIRE', KEYS[1], ARGV[2])
      end
      return current
      ```
      where `ARGV[1] = permitCount` (`1`) and `ARGV[2] = (long)Math.Ceiling(window.TotalMilliseconds)`.
    * If `current <= permitLimit`: returns acquired `RateLimitLease`.
    * If `current > permitLimit`: returns rejected `RateLimitLease` exposing `MetadataName.RetryAfter = retryAfter`.
    * **Fail-Open**: Catches `RedisException` and `TimeoutException`, logs `LogWarning`, and returns an acquired `RateLimitLease` so user traffic is not interrupted during transient Redis outages.

### 5.4. Middleware Pipeline & Localized RFC 7807 `429` Response
In `Program.cs`, middleware order is:
```csharp
app.UseRequestLocalization();
app.UseExceptionHandler();
// OpenAPI & Scalar in Development
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapEndpoints();
```
Placing `UseRateLimiter()` after `UseRequestLocalization()` and `UseAuthentication()` ensures both `CultureInfo.CurrentUICulture` and `HttpContext.User` claims are populated before rate limiting runs.

`RateLimiterOptions.OnRejected`:
1. Sets `httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests` and `ContentType = "application/problem+json"`.
2. If `context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter)`, sets response header `Retry-After` to `Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture)`.
3. Resolves `IStringLocalizer<SharedResources>` from `httpContext.RequestServices` and reads key `"RateLimitExceeded"`:
   * `SharedResources.resx`: `"Too many requests. Please try again later."`
   * `SharedResources.vi.resx`: `"Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau."`
4. Writes standardized RFC 7807 `ProblemDetails`:
   ```json
   {
     "type": "https://tools.ietf.org/html/rfc6585#section-4",
     "title": "Too Many Requests",
     "status": 429,
     "detail": "Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau.",
     "instance": "/api/auth/login",
     "traceId": "00-..."
   }
   ```

---

## 6. Redis Health Check Design

### 6.1. `RedisHealthCheck` (`ExamPrism.Infrastructure.Caching`)
* Implements `IHealthCheck`.
* Registered via `.AddCheck<RedisHealthCheck>("redis")` **only** when `ConnectionStrings:Redis` is non-empty.
* Executes `await multiplexer.GetDatabase().PingAsync()` (respecting cancellation):
  * On success: returns `HealthCheckResult.Healthy()`.
  * On failure / exception: returns `HealthCheckResult.Unhealthy("Redis connection failed.", exception)`.
* Because `GetHealthHandler` aggregates `HealthCheckService.CheckHealthAsync`, `/health` returns `200 OK` (`Status = "Healthy"`) when all registered checks pass, and `503 Service Unavailable` (`Status = "Unhealthy"`) if Redis is configured and unreachable — alerting infrastructure monitoring while Fail-Open keeps user-facing endpoints operational.

---

## 7. Project Layout & File Locations

```
apps/backend/
├── Directory.Packages.props                                    <-- Add HybridCache, StackExchangeRedis, Testcontainers.Redis
├── src/
│   ├── ExamPrism.csproj                                        <-- Add HybridCache & StackExchangeRedis references
│   ├── Program.cs                                              <-- Add app.UseRateLimiter() after UseAuthentication()
│   ├── appsettings.json                                        <-- Add optional ConnectionStrings:Redis, Caching, RateLimiting
│   ├── Common/
│   │   ├── Caching/
│   │   │   └── ICacheService.cs                                <-- Cache service abstraction for Features
│   │   ├── RateLimiting/
│   │   │   └── RateLimitPolicies.cs                            <-- Policy name constants ("auth")
│   │   └── Localization/Resources/
│   │       ├── SharedResources.resx                            <-- Add RateLimitExceeded (en)
│   │       └── SharedResources.vi.resx                         <-- Add RateLimitExceeded (vi)
│   ├── Endpoints/
│   │   ├── Auth/
│   │   │   ├── LoginEndpoint.cs                                <-- Attach .RequireRateLimiting(RateLimitPolicies.Auth)
│   │   │   ├── RegisterEndpoint.cs                             <-- Attach .RequireRateLimiting(RateLimitPolicies.Auth)
│   │   │   └── RefreshEndpoint.cs                              <-- Attach .RequireRateLimiting(RateLimitPolicies.Auth)
│   │   └── Health/
│   │       └── GetHealthEndpoint.cs                            <-- Attach .DisableRateLimiting()
│   └── Infrastructure/
│       ├── DependencyInjection.cs                              <-- Conditional Redis / In-Memory wiring
│       ├── Caching/
│       │   ├── CacheOptions.cs
│       │   ├── HybridCacheService.cs
│       │   ├── ResilientDistributedCache.cs
│       │   └── RedisHealthCheck.cs
│       └── RateLimiting/
│           ├── RateLimitingOptions.cs
│           ├── InMemoryFixedWindowRateLimiter.cs
│           ├── RedisFixedWindowRateLimiter.cs
│           └── RateLimitingSetup.cs
└── tests/
    └── ExamPrism.Tests/
        ├── ExamPrism.Tests.csproj                              <-- Add Testcontainers.Redis reference
        └── Infrastructure/
            ├── CustomWebApplicationFactory.cs                  <-- Raise default test PermitLimits to avoid throttling AuthEndpointTests
            ├── HybridCacheServiceTests.cs                      <-- Unit tests for ICacheService & ResilientDistributedCache fail-open
            ├── RateLimiterUnitTests.cs                         <-- Unit tests for InMemory & Redis rate limiters + FakeTimeProvider + fail-open
            ├── RateLimitingIntegrationTests.cs                 <-- In-memory HTTP integration tests (429 ProblemDetails, vi/en, Retry-After, User vs IP)
            └── RedisIntegrationTests.cs                        <-- Testcontainers.Redis L2 cache, distributed rate limit, and /health check tests
```

---

## 8. Testing & Verification Strategy

1. **Existing Test Suite Protection**:
   * `CustomWebApplicationFactory` leaves `ConnectionStrings:Redis` unconfigured (verifying default In-Memory fallback across the whole suite) and sets `RateLimiting:Global:PermitLimit = 10000` and `RateLimiting:Auth:PermitLimit = 10000` so high-volume functional suites like `AuthEndpointTests` never encounter spurious `429` responses.
2. **Cache Service & Fail-Open Unit Tests (`HybridCacheServiceTests`)**:
   * `GetOrCreateAsync` caches factory output and avoids re-invoking factory within TTL.
   * `GetOrDefaultAsync` returns `default` on cache miss without populating cache, and returns cached value on hit.
   * `SetAsync`, `RemoveAsync`, and `RemoveByTagAsync` mutate/invalidate entries accurately.
   * `ResilientDistributedCache` catches `RedisException` / `TimeoutException`, logs a warning, and allows `HybridCacheService` to continue serving via factory and L1 without throwing.
3. **Rate Limiter Unit Tests (`RateLimiterUnitTests`)**:
   * `InMemoryFixedWindowRateLimiter` enforces `PermitLimit`, computes `RetryAfter`, and resets immediately when `FakeTimeProvider` advances past the window.
   * `RedisFixedWindowRateLimiter` returns rejected lease when Lua script count exceeds `PermitLimit`, resets on `FakeTimeProvider` window advance, and **fails open** (returns acquired lease + logs warning) when `IDatabase.ScriptEvaluateAsync` throws `RedisConnectionException`.
4. **In-Memory Rate Limiting HTTP Integration Tests (`RateLimitingIntegrationTests`)**:
   * Overrides `RateLimiting:Auth:PermitLimit = 2` and `RateLimiting:Global:PermitLimit = 3` via `WithWebHostBuilder`.
   * Verifies `/api/auth/login` allows 2 requests and rejects the 3rd with `429 Too Many Requests`, `Retry-After` header, and localized RFC 7807 `ProblemDetails` in both `vi-VN` and `en-US`.
   * Verifies authenticated requests to `/api/auth/me` partition independently per `UserId` (`user:{id}`) rather than sharing a single IP bucket.
   * Verifies `/health` is never rate-limited even after exceeding `Global` and `Auth` limits.
5. **Real Redis Integration Tests (`RedisIntegrationTests` with `Testcontainers.Redis`)**:
   * Boots `redis:7-alpine` alongside `postgres:17-alpine` and sets `ConnectionStrings:Redis`.
   * Verifies `ICacheService.SetAsync` persists keys into Redis with the `examprism:` prefix and `GetOrDefaultAsync` retrieves them across L2.
   * Verifies HTTP requests enforce rate limits via Redis keys (`examprism:ratelimit:...`) and return `429` when exceeded.
   * Verifies `/health` returns `200 Healthy` when Redis is up.
6. **Architecture & Full Regression**:
   * `dotnet test` passes 100% including all `ArchitectureTests`.
