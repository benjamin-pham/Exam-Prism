# Multi-Stage L7 DDoS Protection Suite Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden `ExamPrism.Infrastructure` and `Common` against Layer-7 DDoS attacks with trusted-proxy IP verification (closing header-spoofing bypasses), Kestrel anti-Slowloris/header limits, a Zip/Brotli decompression-bomb stream guard (`BoundedDecompressedReadStream` & `DecompressionBombGuardMiddleware`), a NAT-safe L1 IP Reputation Penalty Box (`IIpReputationService`, `HybridIpReputationService`, `IpPenaltyBoxMiddleware`) with valid-JWT school exam room exemption, a 3-layer multi-tenant anti-cache-penetration and Catalog DB token-bucket shield (`CatalogMultiTenantStore`), and a dedicated `RateLimitPolicies.TenantOnboarding` policy with full Prometheus telemetry.

**Architecture:** Domain-agnostic contracts (`IIpReputationService`, `IpStrikeReason`, `PayloadTooLargeDecompressionException`, `RateLimitPolicies.TenantOnboarding`, and extended `ExamPrismDiagnostics` counters) reside in `Common.*` with zero references to `ExamPrism.Infrastructure.*`. Concrete DDoS defense services and middlewares reside in `ExamPrism.Infrastructure.*`, operating in Adaptive Dual-Mode (synchronizing bans across containers via `ICacheService` / `ICacheBackplane` when Redis is configured, with zero-latency L1 `ConcurrentDictionary` enforcement and deterministic in-memory fallback in local/test environments).

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core 10 Minimal APIs, `Microsoft.IdentityModel.JsonWebTokens`, `Microsoft.Extensions.Caching.Hybrid` 10.0.0, `StackExchange.Redis` 2.11.0, `OpenTelemetry` 1.18.0, xUnit, FluentAssertions, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-04-ddos-protection-design.md`

## Global Constraints

- Target framework `.NET 10` (`net10.0`); do not add any external NuGet packages outside `apps/backend/Directory.Packages.props`.
- Preserve 100% of `ArchitectureTests`:
  - `Common.*` must never reference `ExamPrism.Infrastructure`, `ExamPrism.Features`, or `ExamPrism.Endpoints`.
  - `ExamPrism.Features.*` must never reference `ExamPrism.Infrastructure.*` interfaces or ASP.NET Core HTTP types.
  - `ExamPrism.Endpoints.*` classes must be `public sealed`, implement `IEndpoint`, execute route logic inline inside `MapEndpoint`, and never reference `DbContext`.
- Backward compatibility with existing tests:
  - In `ReverseProxySetup.IsTrustedProxy`, when `peerIp` is `null` (such as in-memory `TestServer` requests without a physical socket) or loopback (`127.0.0.1` / `::1`) or RFC 1918 private networks (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`), treat the source as a trusted proxy so all existing `EdgeAndHealthTests` and `RateLimitingIntegrationTests` pass without modification.

## Review Focus

1. **IP header spoofing from untrusted public IPs or injection of non-IP garbage strings into rate-limit keys**: `ReverseProxySetup.ResolveClientIp` must reject `CF-Connecting-IP`, `X-Real-IP`, and `X-Forwarded-For` when the peer socket IP (`OriginalRemoteIp` / `Connection.RemoteIpAddress`) is an untrusted public IP (e.g., `198.51.100.77`), increment `ExamPrismDiagnostics.SpoofedIpIgnoredCount`, and validate any header IP candidate with `IPAddress.TryParse` before returning it (tested in Task 1).
2. **School / university computer lab NAT lockout when one unauthenticated browser on the shared campus IP triggers an IP ban**: `IpPenaltyBoxMiddleware` must allow requests bearing a cryptographically valid, non-expired JWT (`Authorization: Bearer <token>` or `access_token` cookie signed with `JwtOptions.SecretKey`) to bypass the IP Penalty Box even while the shared IP is actively banned for unauthenticated requests (tested in Task 2).
3. **Zip/Brotli decompression bomb exhausting container memory before endpoint model binding completes**: `BoundedDecompressedReadStream` and `DecompressionBombGuardMiddleware` must abort reading as soon as cumulative bytes read exceed `RuntimeTuningOptions.MaxDecompressedBodySizeBytes`, return `413 Payload Too Large` (`errorCode = "DECOMPRESSION_BOMB_DETECTED"`), increment `ExamPrismDiagnostics.DecompressionBombRejectedCount`, and immediately ban the client IP via `IIpReputationService.RecordStrikeAsync(..., IpStrikeReason.DecompressionBomb)` (tested in Task 2).
4. **Random-subdomain / `X-Tenant` cache penetration flooding Redis L2 with negative cache keys and saturating `CatalogDbContext`**: `CatalogMultiTenantStore.GetByIdentifierAsync` must short-circuit invalid slug formats via `TenantSlugValidator.IsValidFormat` with 0 cache/DB calls, must set `HybridCacheEntryFlags.DisableDistributedCacheWrite` on negative cache entries so non-existent slugs never write to `IDistributedCache` (Redis L2), and must enforce `MultiTenancyOptions.MaxCatalogDbMissesPerSecond` to cap cold SQL queries during a random-slug storm while `InvalidateTenantAsync` still clears negative cache entries when a tenant is later added (tested in Task 3).
5. **Unauthenticated `429` floods and `/api/auth/login` `401` brute-force attacks repeatedly hitting Redis/PBKDF2 instead of escalating to the L1 Penalty Box**: `RateLimitingSetup.OnRejected` and `IpPenaltyBoxMiddleware` must feed unauthenticated `429` and `401` auth responses into `IIpReputationService.RecordStrikeAsync` so abusive IPs are promoted to the zero-latency L1 ban table, and all 6 new security counters must be exposed on `GET /metrics` (tested in Task 4).

---

### Task 1: Trusted Proxy IP Verification (`ReverseProxySetup`), Kestrel Anti-Slowloris/Header Hardening (`RuntimeTuningSetup`), and Extended DDoS Diagnostics (`ExamPrismDiagnostics`)

**Files:**
- Modify: `apps/backend/src/Common/Telemetry/ExamPrismDiagnostics.cs`
- Modify: `apps/backend/src/Infrastructure/Http/ReverseProxyOptions.cs`
- Modify: `apps/backend/src/Infrastructure/Http/ReverseProxySetup.cs`
- Modify: `apps/backend/src/Infrastructure/Http/RuntimeTuningOptions.cs`
- Modify: `apps/backend/src/Infrastructure/Http/RuntimeTuningSetup.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs`

**Interfaces:**
- Consumes: `Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersOptions`, `Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions`
- Produces:
  - `Common.Telemetry.ExamPrismDiagnostics`:
    - `IpPenaltyBoxRejectedCount` & `RecordIpPenaltyBoxRejection()`
    - `IpBansIssuedCount` & `RecordIpBanIssued()`
    - `DecompressionBombRejectedCount` & `RecordDecompressionBombRejection()`
    - `TenantCatalogShieldRejectedCount` & `RecordTenantCatalogShieldRejection()`
    - `SpoofedIpIgnoredCount` & `RecordSpoofedIpIgnored()`
    - `RateLimitRejectedCount` & `RecordRateLimitRejection()`
  - `ExamPrism.Infrastructure.Http.ReverseProxyOptions`: adds `int ForwardLimit = 2`, `bool TrustAllPrivateNetworks = true`, `List<string> KnownProxies = []`
  - `ExamPrism.Infrastructure.Http.ReverseProxySetup`:
    - `public const string OriginalRemoteIpItemKey = "ExamPrism.OriginalRemoteIp";`
    - `public static bool IsTrustedProxy(IPAddress? peerIp, ReverseProxyOptions? options = null)`
    - `public static string ResolveClientIp(HttpContext context, ReverseProxyOptions? options = null)`
  - `ExamPrism.Infrastructure.Http.RuntimeTuningOptions`: adds `long MaxDecompressedBodySizeBytes = 10 * 1024 * 1024`, `double MinRequestBodyDataRateBytesPerSecond = 240`, `double MinResponseDataRateBytesPerSecond = 240`, `int DataRateGracePeriodSeconds = 5`, `int MaxRequestHeaderCount = 100`, `int MaxRequestHeadersTotalSizeBytes = 32 * 1024`, `int MaxRequestLineSizeBytes = 8 * 1024`

- [ ] **Step 1: Write the failing tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs`**

Create `DdosProtectionSuiteTests` (`namespace ExamPrism.Tests.Infrastructure`) with:
- `ReverseProxySetup_IgnoresSpoofedHeadersFromUntrustedPublicIp_AndHonorsTrustedProxyWithValidatedIp`:
  1. Calls `ExamPrismDiagnostics.ResetForTesting()`.
  2. Creates a `DefaultHttpContext` with `Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.77")` (untrusted public IP) and headers `CF-Connecting-IP = "203.0.113.5"`, `X-Real-IP = "203.0.113.6"`, `X-Forwarded-For = "203.0.113.7"`.
  3. Calls `ReverseProxySetup.ResolveClientIp(ctx)` and asserts it returns `"198.51.100.77"` (ignoring the spoofed headers) and `ExamPrismDiagnostics.SpoofedIpIgnoredCount == 1`.
  4. Creates a `DefaultHttpContext` with `Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.10")` (trusted RFC 1918 proxy) and `CF-Connecting-IP = "not-a-valid-ip"`, `X-Real-IP = "203.0.113.99"`. Asserts `ResolveClientIp` skips the invalid `CF-Connecting-IP` string and returns `"203.0.113.99"`.
- `RuntimeTuningSetup_ConfiguresKestrelAntiSlowlorisDataRates_AndHeaderLimits`:
  1. Builds a `WebApplicationBuilder` with custom `RuntimeTuning` settings (`MinRequestBodyDataRateBytesPerSecond = 500`, `MinResponseDataRateBytesPerSecond = 600`, `DataRateGracePeriodSeconds = 7`, `MaxRequestHeaderCount = 64`, `MaxRequestHeadersTotalSizeBytes = 16384`, `MaxRequestLineSizeBytes = 4096`).
  2. Calls `builder.ConfigureAppKestrel()`, resolves `IOptions<KestrelServerOptions>().Value`, and asserts `Limits.MaxRequestHeaderCount == 64`, `Limits.MaxRequestHeadersTotalSize == 16384`, `Limits.MaxRequestLineSize == 4096`, `Limits.MinRequestBodyDataRate.BytesPerSecond == 500`, `Limits.MinRequestBodyDataRate.GracePeriod == TimeSpan.FromSeconds(7)`, and `Limits.MinResponseDataRate.BytesPerSecond == 600`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~DdosProtectionSuiteTests"`
Expected: FAIL (`SpoofedIpIgnoredCount`, `IsTrustedProxy`, and new `RuntimeTuningOptions` properties do not exist).

- [ ] **Step 3: Implement `ExamPrismDiagnostics` counters, `ReverseProxySetup` anti-spoofing, and `RuntimeTuningSetup` Kestrel limits**

- In `ExamPrismDiagnostics.cs`, add the 6 new counters (`examprism.security.ip_penalty_box_rejected`, `examprism.security.ip_bans_issued`, `examprism.security.decompression_bomb_rejected`, `examprism.security.tenant_catalog_shield_rejected`, `examprism.security.spoofed_ip_ignored`, `examprism.http.rate_limit_rejected`), their thread-safe `Interlocked` backing fields, public `*Count` properties, `Record*()` methods, and reset calls in `ResetForTesting()`.
- In `ReverseProxyOptions.cs`, add `ForwardLimit = 2`, `TrustAllPrivateNetworks = true`, and `KnownProxies = []`.
- In `ReverseProxySetup.cs`:
  - Add `public const string OriginalRemoteIpItemKey = "ExamPrism.OriginalRemoteIp";`.
  - In `UseAppReverseProxy`, register a lightweight middleware before `app.UseForwardedHeaders()` that stores `context.Connection.RemoteIpAddress` in `context.Items[OriginalRemoteIpItemKey]` when non-null.
  - Implement `IsTrustedProxy(IPAddress? peerIp, ReverseProxyOptions? options = null)`: returns `true` when `peerIp is null` or `IPAddress.IsLoopback(peerIp)`, or (after normalizing `IsIPv4MappedToIPv6` via `MapToIPv4()`) when `TrustAllPrivateNetworks` is `true` and `peerIp` is in `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, or `fc00::/7`, or matches `KnownProxies` / `KnownNetworks`.
  - Update `ResolveClientIp(HttpContext context, ReverseProxyOptions? options = null)`: resolves `options ??= context.RequestServices?.GetService<IOptions<ReverseProxyOptions>>()?.Value ?? new ReverseProxyOptions()`. Checks `peerIp = context.Items.TryGetValue(OriginalRemoteIpItemKey, out var origObj) && origObj is IPAddress origIp ? origIp : context.Connection.RemoteIpAddress`. If any of `CF-Connecting-IP`, `X-Real-IP`, or `X-Forwarded-For` is present: if `!options.EnableForwardedHeaders || !IsTrustedProxy(peerIp, options)`, calls `ExamPrismDiagnostics.RecordSpoofedIpIgnored()` and returns `context.Connection.RemoteIpAddress?.ToString() ?? "unknown"`. Otherwise checks `CF-Connecting-IP`, `X-Real-IP`, and the first hop of `X-Forwarded-For` in order, returning `parsedIp.ToString()` for the first candidate where `IPAddress.TryParse(candidate, out var parsedIp)` succeeds.
- In `RuntimeTuningOptions.cs` and `RuntimeTuningSetup.cs`, add the anti-Slowloris and header limit properties and apply them to `kestrel.Limits` in `ConfigureAppKestrel`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~DdosProtectionSuiteTests|FullyQualifiedName~EdgeAndHealthTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Telemetry/ExamPrismDiagnostics.cs apps/backend/src/Infrastructure/Http/ReverseProxyOptions.cs apps/backend/src/Infrastructure/Http/ReverseProxySetup.cs apps/backend/src/Infrastructure/Http/RuntimeTuningOptions.cs apps/backend/src/Infrastructure/Http/RuntimeTuningSetup.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs
git commit -m "feat(http): enforce trusted proxy IP verification and Kestrel anti-Slowloris limits"
```

---

### Task 2: NAT-Safe IP Reputation Service (`IIpReputationService`, `HybridIpReputationService`), `IpPenaltyBoxMiddleware`, and Decompression Bomb Stream Guard (`DecompressionBombGuardMiddleware`)

**Files:**
- Create: `apps/backend/src/Common/Security/IIpReputationService.cs`
- Create: `apps/backend/src/Common/Exceptions/PayloadTooLargeDecompressionException.cs`
- Modify: `apps/backend/src/Common/Exceptions/GlobalExceptionHandler.cs`
- Create: `apps/backend/src/Infrastructure/Security/DdosProtectionOptions.cs`
- Create: `apps/backend/src/Infrastructure/Security/HybridIpReputationService.cs`
- Create: `apps/backend/src/Infrastructure/Security/IpPenaltyBoxMiddleware.cs`
- Create: `apps/backend/src/Infrastructure/Security/DdosProtectionSetup.cs`
- Create: `apps/backend/src/Infrastructure/Http/BoundedDecompressedReadStream.cs`
- Create: `apps/backend/src/Infrastructure/Http/DecompressionBombGuardMiddleware.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs`

**Interfaces:**
- Consumes: `Common.Caching.ICacheService`, `Common.Telemetry.ExamPrismDiagnostics`, `ExamPrism.Infrastructure.Identity.JwtOptions`, `ExamPrism.Infrastructure.Http.ReverseProxySetup`
- Produces:
  - `Common.Security.IpStrikeReason` (`RateLimitExceeded = 1`, `UnknownTenantProbe = 2`, `UnauthorizedAuthAttempt = 3`, `DecompressionBomb = 4`)
  - `Common.Security.IIpReputationService` (`GetBanRemainingAsync`, `RecordStrikeAsync`, `BanIpAsync`, `UnbanIpAsync`)
  - `Common.Exceptions.PayloadTooLargeDecompressionException(long maxAllowedBytes)`
  - `ExamPrism.Infrastructure.Security.DdosProtectionOptions` (`SectionName = "DdosProtection"`)
  - `ExamPrism.Infrastructure.Security.HybridIpReputationService : IIpReputationService`
  - `ExamPrism.Infrastructure.Security.IpPenaltyBoxMiddleware` & `UseIpPenaltyBox(this IApplicationBuilder app)`
  - `ExamPrism.Infrastructure.Security.DdosProtectionSetup.AddAppDdosProtection(this IServiceCollection services, IConfiguration configuration)`
  - `ExamPrism.Infrastructure.Http.BoundedDecompressedReadStream`
  - `ExamPrism.Infrastructure.Http.DecompressionBombGuardMiddleware` & `UseDecompressionBombGuard(this IApplicationBuilder app)`

- [ ] **Step 1: Write the failing tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs`**

Add tests to `DdosProtectionSuiteTests`:
- `HybridIpReputationService_And_IpPenaltyBoxMiddleware_BansAbusiveIp_WhileAllowingValidJwtOnSharedSchoolNatIp`:
  1. Configures `HybridIpReputationService` with `BanThresholdScore = 40`, `UnknownTenantProbeWeight = 20`, `BanDurationSeconds = 120`, and `FakeTimeProvider`.
  2. Records 2 `UnknownTenantProbe` strikes for IP `"203.0.113.50"`: asserts `GetBanRemainingAsync("203.0.113.50")` returns `TimeSpan.FromSeconds(120)` and `ExamPrismDiagnostics.IpBansIssuedCount >= 1`.
  3. Invokes `IpPenaltyBoxMiddleware` with an unauthenticated request from `"203.0.113.50"`: asserts response status is `429 Too Many Requests`, header `X-Penalty-Box` is `"true"`, header `Retry-After` is `"120"`, and `ExamPrismDiagnostics.IpPenaltyBoxRejectedCount >= 1`.
  4. Generates a valid JWT signed with `JwtOptions.SecretKey` (using `TokenService`) and sends a request from the **same banned IP** `"203.0.113.50"` with `Authorization: Bearer <validJwt>`: asserts `IpPenaltyBoxMiddleware` allows the request through (`200 OK`, `_next` called) to protect shared school NAT rooms.
  5. Sends a request with an invalid/tampered JWT from `"203.0.113.50"`: asserts it is blocked with `429 Too Many Requests`.
- `DecompressionBombGuardMiddleware_AbortsStreamExceedingMaxDecompressedSize_Returns413AndBansClientIp`:
  1. Configures `RuntimeTuningOptions` with `MaxDecompressedBodySizeBytes = 1024` (1 KB) and a `HybridIpReputationService`.
  2. Creates a `DefaultHttpContext` from IP `"198.51.100.88"` with a request body containing 4096 bytes of data (simulating a decompressed payload exceeding the 1 KB cap) and invokes `DecompressionBombGuardMiddleware` where the inner delegate attempts to read the request body to completion.
  3. Asserts the read aborts after 1024 bytes, returns `413 Payload Too Large` with `application/problem+json` containing `errorCode = "DECOMPRESSION_BOMB_DETECTED"`, increments `ExamPrismDiagnostics.DecompressionBombRejectedCount`, and immediately bans `"198.51.100.88"` in `IIpReputationService`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~DdosProtectionSuiteTests"`
Expected: FAIL (`IIpReputationService`, `HybridIpReputationService`, `IpPenaltyBoxMiddleware`, `DecompressionBombGuardMiddleware` do not exist).

- [ ] **Step 3: Implement `IIpReputationService`, `HybridIpReputationService`, `IpPenaltyBoxMiddleware`, and `DecompressionBombGuardMiddleware`**

- Create `apps/backend/src/Common/Security/IIpReputationService.cs` with `IpStrikeReason` and `IIpReputationService`.
- Create `apps/backend/src/Common/Exceptions/PayloadTooLargeDecompressionException.cs` (`public sealed class PayloadTooLargeDecompressionException : Exception`) and add a `case PayloadTooLargeDecompressionException decompressionEx:` branch in `GlobalExceptionHandler.cs` returning `StatusCodes.Status413PayloadTooLarge` with `errorCode = "DECOMPRESSION_BOMB_DETECTED"`.
- Create `DdosProtectionOptions.cs`, `HybridIpReputationService.cs`, `IpPenaltyBoxMiddleware.cs`, and `DdosProtectionSetup.cs` in `apps/backend/src/Infrastructure/Security/`:
  - `HybridIpReputationService` uses bounded `ConcurrentDictionary<string, long> _localBans` and `ConcurrentDictionary<string, StrikeState> _strikes` (capped at `MaxTrackedIps = 10_000`), `TimeProvider`, and optional `ICacheService`.
  - `IpPenaltyBoxMiddleware`:
    - Exempts `/health*`, `/metrics`, `/openapi*`, `/scalar*`.
    - When `GetBanRemainingAsync(clientIp)` is non-null: if `options.BypassPenaltyBoxForAuthenticatedJwt` is `true` and `HasValidJwtSignature(context, jwtOptions)` succeeds (validates `Bearer` token or `access_token` cookie signature and lifetime via `JsonWebTokenHandler` and `SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SecretKey))`), allows request through; otherwise writes `429 Too Many Requests` with `Retry-After`, `X-Penalty-Box: true`, and `ProblemDetails`.
    - After `await _next(context)`, if `context.Response.StatusCode == StatusCodes.Status401Unauthorized` and path starts with `/api/auth/` or `/api/platform/auth/`, calls `await reputationService.RecordStrikeAsync(clientIp, IpStrikeReason.UnauthorizedAuthAttempt)`.
- Create `BoundedDecompressedReadStream.cs` and `DecompressionBombGuardMiddleware.cs` in `apps/backend/src/Infrastructure/Http/`:
  - `BoundedDecompressedReadStream` wraps `Stream` and throws `PayloadTooLargeDecompressionException` (and increments `ExamPrismDiagnostics.RecordDecompressionBombRejection()`) as soon as total bytes read exceed `maxAllowedBytes`.
  - `DecompressionBombGuardMiddleware` wraps `context.Request.Body` in `BoundedDecompressedReadStream` when `MaxDecompressedBodySizeBytes > 0`. When `PayloadTooLargeDecompressionException` is caught, records `IpStrikeReason.DecompressionBomb` on `IIpReputationService` and writes `413 Payload Too Large` `ProblemDetails` (with `errorCode = "DECOMPRESSION_BOMB_DETECTED"` and `traceId`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~DdosProtectionSuiteTests|FullyQualifiedName~GlobalExceptionHandler"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Security/IIpReputationService.cs apps/backend/src/Common/Exceptions/PayloadTooLargeDecompressionException.cs apps/backend/src/Common/Exceptions/GlobalExceptionHandler.cs apps/backend/src/Infrastructure/Security apps/backend/src/Infrastructure/Http/BoundedDecompressedReadStream.cs apps/backend/src/Infrastructure/Http/DecompressionBombGuardMiddleware.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs
git commit -m "feat(security): add NAT-safe IP Penalty Box and Decompression Bomb stream guard"
```

---

### Task 3: Anti-Cache-Penetration & Catalog DB Shield (`CatalogMultiTenantStore` & `TenantAccessGuardMiddleware`)

**Files:**
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/MultiTenancyOptions.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/TenantAccessGuardMiddleware.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs`

**Interfaces:**
- Consumes: `Common.MultiTenancy.TenantSlugValidator`, `Common.Security.IIpReputationService`, `Common.Telemetry.ExamPrismDiagnostics`
- Produces:
  - `ExamPrism.Infrastructure.MultiTenancy.MultiTenancyOptions`: adds `int MaxNegativeCacheEntries { get; set; } = 2000;`, `int MaxCatalogDbMissesPerSecond { get; set; } = 50;`
  - Hardened `CatalogMultiTenantStore.GetByIdentifierAsync`, `GetAsync`, and `InvalidateTenantAsync`
  - Strike-recording `TenantAccessGuardMiddleware`

- [ ] **Step 1: Write the failing tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs`**

Add tests to `DdosProtectionSuiteTests`:
- `CatalogMultiTenantStore_RejectsInvalidSlugWithoutCacheOrDb_KeepsNegativeCacheOutOfDistributedCache_AndEnforcesDbMissBudget`:
  1. Sets up `CatalogMultiTenantStore` with a `RecordingDistributedCache`, a query-counting `DbCommandInterceptor` returning empty results, and `MultiTenancyOptions { MaxNegativeCacheEntries = 100, MaxCatalogDbMissesPerSecond = 5, NotFoundCacheExpirationSeconds = 30 }`.
  2. Calls `GetByIdentifierAsync("INVALID_SLUG_$$$")` and `GetByIdentifierAsync("-bad-slug-")`: asserts both return `null`, DB query count is `0`, and `RecordingDistributedCache` has `0` entries.
  3. Calls `GetByIdentifierAsync("missing-tenant-1")`: asserts it executes `1` DB query, caches the negative result in L1 so a second call to `GetByIdentifierAsync("missing-tenant-1")` executes `0` additional DB queries, AND asserts `RecordingDistributedCache` still contains `0` entries (proving `DisableDistributedCacheWrite` kept negative cache out of Redis L2!).
  4. Fires 20 distinct valid-format unknown slugs (`"random-slug-0"` .. `"random-slug-19"`) within the same second: asserts total DB queries executed are capped at `5` (`MaxCatalogDbMissesPerSecond == 5`) and `ExamPrismDiagnostics.TenantCatalogShieldRejectedCount >= 15`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~DdosProtectionSuiteTests"`
Expected: FAIL (invalid slugs currently query DB, negative cache writes to `IDistributedCache`, and `MaxCatalogDbMissesPerSecond` does not exist).

- [ ] **Step 3: Implement 3-layer anti-penetration shield in `CatalogMultiTenantStore` and strike hook in `TenantAccessGuardMiddleware`**

- In `MultiTenancyOptions.cs`, add `public int MaxNegativeCacheEntries { get; set; } = 2000;` and `public int MaxCatalogDbMissesPerSecond { get; set; } = 50;`.
- In `CatalogMultiTenantStore.cs`:
  - At the start of `GetByIdentifierAsync`, after normalizing `slug`, check `if (!TenantSlugValidator.IsValidFormat(slug)) return null;`.
  - Maintain a bounded `ConcurrentDictionary<string, long> _negativeCache = new()` (`key -> expiresAtUnixMs`) and per-second miss window state (`_missWindowSecond`, `_missCountInSecond`).
  - Before acquiring the stripe lock in `GetByIdentifierAsync` and `GetAsync`, check `IsNegativelyCached(key)`: if `true`, return `null` immediately. Inside the stripe lock, re-check `IsNegativelyCached(key)`.
  - Before querying `_db.Tenants`, check `TryAcquireCatalogDbMissPermit()`: if `MaxCatalogDbMissesPerSecond > 0` and the current second's miss count exceeds `MaxCatalogDbMissesPerSecond`, call `ExamPrismDiagnostics.RecordTenantCatalogShieldRejection()` and return `null` immediately without querying `_db` and without caching a false negative.
  - When `_db.Tenants` returns `null`, record `key` in `_negativeCache` (evicting expired or oldest entries when `Count >= _options.MaxNegativeCacheEntries`) and set `Flags = HybridCacheEntryFlags.DisableDistributedCacheWrite` on `notFoundOptions` so `_hybridCache.SetAsync` only populates L1 memory and never writes negative entries to Redis L2.
  - In `InvalidateTenantAsync`, remove all invalidated keys from `_negativeCache` in addition to `_hybridCache`.
- In `TenantAccessGuardMiddleware.cs`, when `resolution.Kind == HostResolutionKind.InvalidTenantHost` or `tenantInfo is null`, resolve `IIpReputationService?` from `context.RequestServices` and record `IpStrikeReason.UnknownTenantProbe` for `ReverseProxySetup.ResolveClientIp(context)`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~DdosProtectionSuiteTests|FullyQualifiedName~SubdomainAndCatalogStoreTests|FullyQualifiedName~AntiDdosCacheAndOutputCacheTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/MultiTenancy/MultiTenancyOptions.cs apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs apps/backend/src/Infrastructure/MultiTenancy/TenantAccessGuardMiddleware.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs
git commit -m "feat(multitenancy): add 3-layer anti-penetration catalog shield and L1-only negative cache"
```

---

### Task 4: `RateLimitPolicies.TenantOnboarding`, RateLimiter Strike Hook, Prometheus `/metrics` Exposure, `Program.cs` Pipeline Wiring & End-to-End Integration

**Files:**
- Modify: `apps/backend/src/Common/RateLimiting/RateLimitPolicies.cs`
- Modify: `apps/backend/src/Infrastructure/RateLimiting/RateLimitingOptions.cs`
- Modify: `apps/backend/src/Infrastructure/RateLimiting/RateLimitingSetup.cs`
- Modify: `apps/backend/src/Endpoints/Tenants/ProvisionTenantPublicEndpoint.cs`
- Modify: `apps/backend/src/Infrastructure/Telemetry/TelemetrySetup.cs`
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs`
- Modify: `apps/backend/src/Program.cs`
- Modify: `apps/backend/src/appsettings.json`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs`

**Interfaces:**
- Consumes: `Common.RateLimiting.RateLimitPolicies`, `Common.Security.IIpReputationService`, `Common.Telemetry.ExamPrismDiagnostics`, `ExamPrism.Infrastructure.Security.DdosProtectionSetup`, `ExamPrism.Infrastructure.Http.DecompressionBombGuardMiddleware`
- Produces:
  - `Common.RateLimiting.RateLimitPolicies.TenantOnboarding = "TenantOnboarding"`
  - `ExamPrism.Infrastructure.RateLimiting.RateLimitingOptions.TenantOnboarding` (`SlidingWindowPolicyOptions`)
  - Updated `TelemetrySetup` exposing all 6 new DDoS metrics on `GET /metrics`
  - Updated `DependencyInjection.AddInfrastructure` and `Program.cs` middleware pipeline

- [ ] **Step 1: Write the failing integration test in `apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs`**

Add integration test to `DdosProtectionSuiteTests` (using `CustomWebApplicationFactory`):
- `EndToEndPipeline_ExposesDdosPrometheusMetrics_EnforcesTenantOnboardingPolicy_AndEscalatesRepeatedViolationsToPenaltyBox`:
  1. Creates a test client via `_factory.WithWebHostBuilder(...)` with `DdosProtection:BanThresholdScore = 30`, `DdosProtection:UnknownTenantProbeWeight = 15`.
  2. Sends 2 requests with non-existent tenant header `X-Tenant: unknown-probe-1` and `X-Tenant: unknown-probe-2` from client IP `10.0.0.1` (`X-Forwarded-For: 203.0.113.200`): both return `404 Not Found` and accumulate `30` strike points.
  3. Sends a 3rd request from `X-Forwarded-For: 203.0.113.200`: asserts it is intercepted early by `IpPenaltyBoxMiddleware` with `429 Too Many Requests` and header `X-Penalty-Box: true`.
  4. Calls `GET /metrics` and asserts the Prometheus response body contains `examprism_security_ip_penalty_box_rejected_total`, `examprism_security_ip_bans_issued_total`, `examprism_security_decompression_bomb_rejected_total`, `examprism_security_tenant_catalog_shield_rejected_total`, `examprism_security_spoofed_ip_ignored_total`, and `examprism_http_rate_limit_rejected_total`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~DdosProtectionSuiteTests"`
Expected: FAIL (`IpPenaltyBoxMiddleware` not yet wired into `Program.cs` and new Prometheus metric names not yet in `TelemetrySetup.cs`).

- [ ] **Step 3: Wire `TenantOnboarding` rate limit policy, `OnRejected` strike hook, Prometheus `/metrics`, `DependencyInjection.cs`, `Program.cs`, and `appsettings.json`**

- In `RateLimitPolicies.cs`, add `public const string TenantOnboarding = "TenantOnboarding";`.
- In `RateLimitingOptions.cs`, add `public SlidingWindowPolicyOptions TenantOnboarding { get; set; } = new(PermitLimit: 5, WindowSeconds: 60, SegmentsPerWindow: 6);`.
- In `RateLimitingSetup.cs`:
  - Register `RateLimitPolicies.TenantOnboarding` partitioned by `platform:onboarding:ip:{GetClientIp(httpContext)}` using `RedisSlidingWindowRateLimiter` / `InMemorySlidingWindowRateLimiter`.
  - In `options.OnRejected`, call `ExamPrismDiagnostics.RecordRateLimitRejection()`, and if `context.HttpContext.User.Identity?.IsAuthenticated != true`, resolve `IIpReputationService?` from `context.HttpContext.RequestServices` and call `await reputation.RecordStrikeAsync(GetClientIp(context.HttpContext), IpStrikeReason.RateLimitExceeded, cancellationToken: cancellationToken)`.
- In `ProvisionTenantPublicEndpoint.cs`, update `.RequireRateLimiting(RateLimitPolicies.Auth)` to `.RequireRateLimiting(RateLimitPolicies.TenantOnboarding)` and add `.WithRequestPriority(RequestPriority.Low)`.
- In `TelemetrySetup.cs`, append the 6 new Prometheus counters (`examprism_security_ip_penalty_box_rejected_total`, `examprism_security_ip_bans_issued_total`, `examprism_security_decompression_bomb_rejected_total`, `examprism_security_tenant_catalog_shield_rejected_total`, `examprism_security_spoofed_ip_ignored_total`, `examprism_http_rate_limit_rejected_total`) to the `/metrics` response builder.
- In `DependencyInjection.cs`, call `services.AddAppDdosProtection(configuration);`.
- In `Program.cs`, update middleware order to match Section 5 of the spec (`UseAppReverseProxy` -> `UseAppLocalization` -> `UseExceptionHandler` -> `UseIpPenaltyBox` -> `UseRouting` -> `UseAdaptiveLoadShedding` -> `UseRequestTimeouts` -> `UseRequestDecompression` -> `UseDecompressionBombGuard` -> `UseResponseCompression` -> ...).
- In `appsettings.json`, add the default `DdosProtection`, `RuntimeTuning`, `MultiTenancy`, and `RateLimiting:TenantOnboarding` configuration values from Section 3 of the spec.

- [ ] **Step 4: Run full test suite to verify 100% pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~DdosProtectionSuiteTests|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~RateLimitingIntegrationTests|FullyQualifiedName~EdgeAndHealthTests"`
Expected: PASS (0 failures).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/RateLimiting/RateLimitPolicies.cs apps/backend/src/Infrastructure/RateLimiting/RateLimitingOptions.cs apps/backend/src/Infrastructure/RateLimiting/RateLimitingSetup.cs apps/backend/src/Endpoints/Tenants/ProvisionTenantPublicEndpoint.cs apps/backend/src/Infrastructure/Telemetry/TelemetrySetup.cs apps/backend/src/Infrastructure/DependencyInjection.cs apps/backend/src/Program.cs apps/backend/src/appsettings.json apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs
git commit -m "feat(ddos): wire L7 DDoS protection pipeline, TenantOnboarding policy, and Prometheus metrics"
```
