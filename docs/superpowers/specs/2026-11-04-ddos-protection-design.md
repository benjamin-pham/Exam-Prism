# Design Specification: Multi-Stage L7 DDoS Protection Suite (NAT-Safe & Anti-Penetration Edition)

**Date:** 2026-11-04  
**Status:** Draft for Review  
**Scope:** Architectural Subsystem (`Common.*`, `ExamPrism.Infrastructure.*`, `ExamPrism.Endpoints.*`)  

---

## 1. Context and Goals

Building upon the High-Load Infrastructure Suites v1 (`2026-11-04-high-load-infrastructure-design.md`) and v2 (`2026-11-04-high-load-infrastructure-v2-design.md`), ExamPrism requires a dedicated **In-App Layer-7 DDoS Protection Suite** to close six critical attack vectors while preserving 100% availability for legitimate examination rooms where hundreds of candidates share a single school/campus NAT IP address:

1. **Trusted Proxy IP Verification & Anti-Spoofing (`ReverseProxySetup`):**
   * **Vulnerability:** `ReverseProxySetup.ResolveClientIp(HttpContext)` previously trusted `CF-Connecting-IP`, `X-Real-IP`, and `X-Forwarded-For` unconditionally without verifying whether `Connection.RemoteIpAddress` belonged to a trusted proxy network, allowing attackers to rotate spoofed headers and bypass all IP-based rate limiters.
   * **Defense:** Validate that the peer socket IP (`OriginalRemoteIp` / `Connection.RemoteIpAddress`) belongs to a configured trusted proxy (`Loopback`, RFC 1918 private networks when `TrustAllPrivateNetworks` is enabled, or configured `KnownProxies` / `KnownNetworks` CIDRs) before accepting forwarded IP headers, and validate parsed header values with `IPAddress.TryParse`.

2. **Kestrel Anti-Slowloris, Slow-Post, Slow-Read & Header Flood Hardening (`RuntimeTuningSetup`):**
   * **Defense:** Enforce `MinRequestBodyDataRate`, `MinResponseDataRate`, `MaxRequestHeaderCount`, `MaxRequestHeadersTotalSize`, and `MaxRequestLineSize` at the Kestrel transport layer.

3. **Decompression Bomb (Zip/Brotli Bomb) Stream Guard (`DecompressionBombGuardMiddleware`):**
   * **Vulnerability:** `UseRequestDecompression()` previously ran at the very front of the middleware pipeline without a decompressed byte cap, enabling a tiny compressed payload (e.g., 10 KB gzip) to expand into gigabytes in memory.
   * **Defense:** Reorder `UseRequestDecompression()` behind early DDoS gates (`UseIpPenaltyBox()` and `UseAdaptiveLoadShedding()`) and wrap decompressed request streams in a `BoundedDecompressedReadStream` that aborts immediately with `413 Payload Too Large` (`PayloadTooLargeDecompressionException`) and bans the offending IP if decompressed bytes exceed `MaxDecompressedBodySizeBytes`.

4. **NAT-Safe IP Reputation & L1 Penalty Box (`IIpReputationService` & `IpPenaltyBoxMiddleware`):**
   * **Defense:** Track per-IP malicious behavior strikes (`RateLimitExceeded`, `UnknownTenantProbe`, `UnauthorizedAuthAttempt`, `DecompressionBomb`) across a sliding window. When an IP crosses `BanThresholdScore`, ban it in a zero-latency L1 in-memory table synchronized cluster-wide via `HybridCache` + `ICacheBackplane`.
   * **School/Campus NAT Safety Guarantee:** Requests carrying a cryptographically valid JWT signature (`Bearer` header or `access_token` cookie) bypass the IP Penalty Box and never accumulate IP-level strikes, ensuring that 200 students taking an exam behind a shared school NAT IP are never locked out even if an unauthenticated device on the same network triggers an IP ban.

5. **Anti-Cache-Penetration & Catalog DB Shield (`CatalogMultiTenantStore` & `TenantAccessGuardMiddleware`):**
   * **Vulnerability:** A botnet sending thousands of unique random `X-Tenant` headers or random subdomains bypasses single-key coalescing, saturates `CatalogDbContext` with cold SQL queries, and pollutes Redis L2 cache with tens of thousands of negative-cache entries.
   * **Defense:**
     1. Zero-allocation slug format validation (`TenantSlugValidator.IsValidFormat`) before touching `HybridCache` or database locks.
     2. L1-only bounded negative caching (`HybridCacheEntryFlags.DisableDistributedCacheWrite` + bounded in-memory negative-key tracking) so non-existent tenant lookups never pollute Redis L2.
     3. Atomic Token Bucket (`MaxCatalogDbMissesPerSecond`) bounding cold SQL queries to `CatalogDbContext` during random-subdomain floods, plus automatic `UnknownTenantProbe` strike recording in `TenantAccessGuardMiddleware`.

6. **Public Heavy Endpoint Hardening (`RateLimitPolicies.TenantOnboarding`):**
   * **Defense:** Isolate `POST /api/tenants` (which triggers background PostgreSQL `CREATE DATABASE` and EF Core migrations) onto a strict sliding-window policy (`RateLimitPolicies.TenantOnboarding`) and wire `RateLimitingSetup.OnRejected` (`429`) and unauthenticated `401` auth failures into `IIpReputationService`.

---

## 2. Architecture & Layering Rules

To preserve 100% compliance with `ArchitectureTests`:
1. **`Common.*` (`apps/backend/src/Common`)**:
   * Houses public abstractions, enums, exceptions, policies, and telemetry counters (`IIpReputationService`, `IpStrikeReason`, `PayloadTooLargeDecompressionException`, `RateLimitPolicies.TenantOnboarding`, `ExamPrismDiagnostics`).
   * Must **never** reference `ExamPrism.Infrastructure.*`, `ExamPrism.Features.*`, or `ExamPrism.Endpoints.*`.
2. **`ExamPrism.Infrastructure.*` (`apps/backend/src/Infrastructure`)**:
   * Houses all concrete implementations, middleware, stream wrappers, options, and DI setup extensions (`HybridIpReputationService`, `IpPenaltyBoxMiddleware`, `DecompressionBombGuardMiddleware`, `BoundedDecompressedReadStream`, `DdosProtectionOptions`, `DdosProtectionSetup`).
3. **`ExamPrism.Features.*` & `ExamPrism.Endpoints.*`**:
   * Feature handlers and endpoints maintain all existing `ArchitectureTests` invariants.

---

## 3. Configuration Schema (`appsettings.json` Additions)

```json
{
  "ReverseProxy": {
    "EnableForwardedHeaders": true,
    "ForwardLimit": 2,
    "TrustAllPrivateNetworks": true,
    "KnownProxies": [],
    "KnownNetworks": [ "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" ]
  },
  "RuntimeTuning": {
    "MinWorkerThreads": 64,
    "MinCompletionPortThreads": 64,
    "MaxConcurrentConnections": 50000,
    "MaxRequestBodySize": 10485760,
    "MaxDecompressedBodySizeBytes": 10485760,
    "MaxHttp2StreamsPerConnection": 250,
    "KeepAliveTimeoutSeconds": 120,
    "RequestHeadersTimeoutSeconds": 30,
    "MinRequestBodyDataRateBytesPerSecond": 240,
    "MinResponseDataRateBytesPerSecond": 240,
    "DataRateGracePeriodSeconds": 5,
    "MaxRequestHeaderCount": 100,
    "MaxRequestHeadersTotalSizeBytes": 32768,
    "MaxRequestLineSizeBytes": 8192
  },
  "DdosProtection": {
    "Enabled": true,
    "BanThresholdScore": 100,
    "StrikeWindowSeconds": 60,
    "BanDurationSeconds": 300,
    "MaxTrackedIps": 10000,
    "BypassPenaltyBoxForAuthenticatedJwt": true,
    "RateLimitExceededWeight": 15,
    "UnknownTenantProbeWeight": 20,
    "UnauthorizedAuthAttemptWeight": 15,
    "DecompressionBombWeight": 100
  },
  "MultiTenancy": {
    "MaxNegativeCacheEntries": 2000,
    "MaxCatalogDbMissesPerSecond": 50
  },
  "RateLimiting": {
    "TenantOnboarding": {
      "PermitLimit": 5,
      "WindowSeconds": 60,
      "SegmentsPerWindow": 6
    }
  }
}
```

---

## 4. Detailed Subsystem Designs

### 4.1. Subsystem 1: Trusted Proxy IP Verification & Anti-Spoofing (`ExamPrism.Infrastructure.Http`)

#### 4.1.1. `ReverseProxyOptions` & `ReverseProxySetup`
* **`ReverseProxyOptions` (`ExamPrism.Infrastructure.Http`):**
  * Properties:
    * `bool EnableForwardedHeaders { get; set; } = true`
    * `int ForwardLimit { get; set; } = 2`
    * `bool TrustAllPrivateNetworks { get; set; } = true`
    * `List<string> KnownProxies { get; set; } = []`
    * `List<string> KnownNetworks { get; set; } = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]`
* **Original Peer Tracking & Trusted Proxy Verification:**
  * `UseAppReverseProxy(this IApplicationBuilder app)` registers a lightweight pre-middleware before `app.UseForwardedHeaders()` that stores `context.Connection.RemoteIpAddress` in `context.Items["ExamPrism.OriginalRemoteIp"]`.
  * `ReverseProxySetup.IsTrustedProxy(IPAddress? peerIp, ReverseProxyOptions? options = null)`:
    * If `peerIp is null` (e.g., in-memory `TestServer` without a physical socket) or `IPAddress.IsLoopback(peerIp)`, returns `true`.
    * Normalizes IPv4-mapped IPv6 addresses (`peerIp.IsIPv4MappedToIPv6 ? peerIp.MapToIPv4() : peerIp`).
    * Checks RFC 1918 / IPv6 Unique Local private ranges (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `fc00::/7`) when `TrustAllPrivateNetworks` is `true`, plus any configured `KnownProxies` and `KnownNetworks`.
  * `ReverseProxySetup.ResolveClientIp(HttpContext context, ReverseProxyOptions? options = null)`:
    * Determines `peerIp = context.Items["ExamPrism.OriginalRemoteIp"] as IPAddress ?? context.Connection.RemoteIpAddress`.
    * Checks if forwarded headers (`CF-Connecting-IP`, `X-Real-IP`, `X-Forwarded-For`) are present on the request.
    * If forwarded headers are present:
      * When `(options?.EnableForwardedHeaders ?? true)` is `true` **AND** `IsTrustedProxy(peerIp, options)` is `true`:
        * Evaluates `CF-Connecting-IP`, then `X-Real-IP`, then the first hop of `X-Forwarded-For`.
        * Each candidate string is trimmed and validated via `IPAddress.TryParse(candidate, out var parsedIp)`. Only valid IP addresses are returned (`parsedIp.ToString()`).
      * When `IsTrustedProxy(peerIp, options)` is `false` (an untrusted external client attempting to spoof `CF-Connecting-IP`, `X-Real-IP`, or `X-Forwarded-For`):
        * Ignores all client-supplied forwarding headers, increments `ExamPrismDiagnostics.RecordSpoofedIpIgnored()`, and falls back directly to `context.Connection.RemoteIpAddress?.ToString() ?? "unknown"`.

---

### 4.2. Subsystem 2: Kestrel Anti-Slowloris & Decompression Bomb Shield (`ExamPrism.Infrastructure.Http`)

#### 4.2.1. Kestrel Transport Hardening (`RuntimeTuningOptions` & `RuntimeTuningSetup`)
* **`RuntimeTuningOptions` additions:**
  * `long MaxDecompressedBodySizeBytes { get; set; } = 10 * 1024 * 1024;` (10 MB)
  * `double MinRequestBodyDataRateBytesPerSecond { get; set; } = 240;`
  * `double MinResponseDataRateBytesPerSecond { get; set; } = 240;`
  * `int DataRateGracePeriodSeconds { get; set; } = 5;`
  * `int MaxRequestHeaderCount { get; set; } = 100;`
  * `int MaxRequestHeadersTotalSizeBytes { get; set; } = 32 * 1024;` (32 KB)
  * `int MaxRequestLineSizeBytes { get; set; } = 8 * 1024;` (8 KB)
* **`RuntimeTuningSetup.ConfigureAppKestrel`:**
  * Applies `MaxRequestHeaderCount`, `MaxRequestHeadersTotalSize`, `MaxRequestLineSize`, `MinRequestBodyDataRate` (`new MinDataRate(options.MinRequestBodyDataRateBytesPerSecond, TimeSpan.FromSeconds(options.DataRateGracePeriodSeconds))` when `> 0`), and `MinResponseDataRate`.

#### 4.2.2. `BoundedDecompressedReadStream` & `DecompressionBombGuardMiddleware`
* **`PayloadTooLargeDecompressionException` (`Common/Exceptions/PayloadTooLargeDecompressionException.cs`):**
  * Sealed exception carrying `long MaxAllowedBytes`.
  * Mapped in `GlobalExceptionHandler` to `413 Payload Too Large` (`StatusCodes.Status413PayloadTooLarge`) with `errorCode = "DECOMPRESSION_BOMB_DETECTED"`.
* **`BoundedDecompressedReadStream` (`ExamPrism.Infrastructure.Http`):**
  * Wraps `Stream inner` and tracks `_totalBytesRead`.
  * On `Read` / `ReadAsync`, if `_totalBytesRead > _maxAllowedBytes`, increments `ExamPrismDiagnostics.RecordDecompressionBombRejection()`, invokes `_onLimitExceeded?.Invoke()`, and throws `PayloadTooLargeDecompressionException(_maxAllowedBytes)`.
* **`DecompressionBombGuardMiddleware` (`ExamPrism.Infrastructure.Http`):**
  * Placed immediately after `app.UseRequestDecompression()`.
  * Wraps `context.Request.Body` in `BoundedDecompressedReadStream` whenever the request had a `Content-Encoding` header (captured before `UseRequestDecompression()` strips `Content-Encoding`, or unconditionally for all request bodies with `MaxDecompressedBodySizeBytes > 0`).
  * Catches `PayloadTooLargeDecompressionException` (or delegates to `GlobalExceptionHandler` while recording `await ipReputation.RecordStrikeAsync(clientIp, IpStrikeReason.DecompressionBomb)` so the offending IP is immediately banned in the L1 Penalty Box) and writes a `413 Payload Too Large` RFC 7807 `ProblemDetails` response.

---

### 4.3. Subsystem 3: NAT-Safe IP Reputation & L1 Penalty Box (`Common.Security` & `ExamPrism.Infrastructure.Security`)

#### 4.3.1. Abstractions (`Common/Security/IIpReputationService.cs`)
```csharp
namespace Common.Security;

public enum IpStrikeReason
{
    RateLimitExceeded = 1,
    UnknownTenantProbe = 2,
    UnauthorizedAuthAttempt = 3,
    DecompressionBomb = 4
}

public interface IIpReputationService
{
    ValueTask<TimeSpan?> GetBanRemainingAsync(string ipAddress, CancellationToken cancellationToken = default);
    ValueTask RecordStrikeAsync(string ipAddress, IpStrikeReason reason, int customWeight = 0, CancellationToken cancellationToken = default);
    ValueTask BanIpAsync(string ipAddress, TimeSpan duration, CancellationToken cancellationToken = default);
    ValueTask UnbanIpAsync(string ipAddress, CancellationToken cancellationToken = default);
}
```

#### 4.3.2. `HybridIpReputationService` (`ExamPrism.Infrastructure.Security`)
* Maintains two bounded, lock-free `ConcurrentDictionary` tables in L1 memory (capped at `DdosProtectionOptions.MaxTrackedIps = 10_000` with expired/oldest eviction):
  1. `_localBans`: `ip -> banExpiresAtUnixMs`
  2. `_strikeCounters`: `ip -> StrikeBucket(Score, WindowStartUnixMs)`
* Also uses `ICacheService` (`HybridCacheService`, which propagates key invalidations across containers via `ICacheBackplane`) under key `ddos:ban:ip:{ip}`:
  * `GetBanRemainingAsync(ip)`:
    * Fast path: checks `_localBans`. If banned and `nowMs < banExpiresAtUnixMs`, returns remaining `TimeSpan`.
    * Checks `ICacheService.GetOrDefaultAsync<long?>($"ddos:ban:ip:{ip}")` only when needed or syncs via backplane. To ensure 0 Redis calls on normal unbanned traffic, `_localBans` acts as the primary L1 check, and `BanIpAsync` writes to `ICacheService` + publishes backplane key notification so peer containers populate/refresh their state.
  * `RecordStrikeAsync(ip, reason, customWeight)`:
    * Ignores empty or `"unknown"` IPs.
    * Resolves strike weight from `DdosProtectionOptions` (`DecompressionBomb = 100`, `UnknownTenantProbe = 20`, `RateLimitExceeded = 15`, `UnauthorizedAuthAttempt = 15`).
    * Atomically adds weight to the IP's current window bucket in `_strikeCounters`.
    * If accumulated score `>= DdosProtectionOptions.BanThresholdScore`, calls `await BanIpAsync(ip, TimeSpan.FromSeconds(options.BanDurationSeconds))`.
  * `BanIpAsync(ip, duration)`:
    * Stores expiry in `_localBans[ip]`, writes to `ICacheService`, and increments `ExamPrismDiagnostics.RecordIpBanIssued()`.

#### 4.3.3. `IpPenaltyBoxMiddleware` (`ExamPrism.Infrastructure.Security`)
* Placed right after `app.UseAppReverseProxy()`.
* **Execution Flow:**
  1. If `!options.Enabled` or path is an infrastructure endpoint (`/health*`, `/metrics`, `/openapi*`, `/scalar*`), calls `await _next(context)` and returns.
  2. Resolves `clientIp = ReverseProxySetup.ResolveClientIp(context, reverseProxyOptions)`.
  3. Checks `var banRemaining = await reputationService.GetBanRemainingAsync(clientIp, context.RequestAborted)`.
  4. If `banRemaining.HasValue`:
     * **NAT-Safe Valid-JWT Bypass:** If `options.BypassPenaltyBoxForAuthenticatedJwt` is `true` and `HasValidJwtSignature(context, jwtSettings)` returns `true` (validates the `Bearer` header or `access_token` cookie signature and expiration using `JsonWebTokenHandler` and the configured symmetric signing key without any DB query), the request **bypasses the IP ban** and proceeds!
     * Otherwise, increments `ExamPrismDiagnostics.RecordIpPenaltyBoxRejection()`, sets `Retry-After: {seconds}`, sets `X-Penalty-Box: true`, and returns `429 Too Many Requests` with RFC 7807 `ProblemDetails` (`errorCode = "IP_PENALTY_BOX_BANNED"`).
  5. Calls `await _next(context)`.
  6. **Post-Request Brute-Force Strike Hook:** If the request targeted an authentication endpoint (`/api/auth/*` or `/api/platform/auth/*`) and resulted in `401 Unauthorized`, records `await reputationService.RecordStrikeAsync(clientIp, IpStrikeReason.UnauthorizedAuthAttempt)`.

---

### 4.4. Subsystem 4: Anti-Cache-Penetration & Catalog DB Shield (`ExamPrism.Infrastructure.MultiTenancy`)

#### 4.4.1. Three-Layer Protection in `CatalogMultiTenantStore`
1. **Zero-Allocation Slug Format Pre-Filter:**
   * At the top of `GetByIdentifierAsync(string identifier)`, after normalizing `slug = identifier.Trim().ToLowerInvariant()`, checks `if (!TenantSlugValidator.IsValidFormat(slug)) return null;`.
   * Invalid slugs never touch `HybridCache`, `KeyStripes`, or `CatalogDbContext`.
2. **L1-Only Bounded Negative Caching:**
   * Maintains a bounded `ConcurrentDictionary<string, long> _negativeCache` (capped at `MultiTenancyOptions.MaxNegativeCacheEntries = 2000`) storing `key -> expiresAtUnixMs`.
   * Checks `_negativeCache` before acquiring `KeyStripes`.
   * When a tenant is not found in `CatalogDbContext`:
     * Stores the miss in `_negativeCache` (evicting expired/oldest entries if `Count >= MaxNegativeCacheEntries`).
     * Sets `HybridCacheEntryFlags.DisableDistributedCacheWrite` on `notFoundOptions` when calling `_hybridCache.SetAsync` so random non-existent tenant lookups **never** write to Redis L2!
   * In `InvalidateTenantAsync(Guid tenantId, string slug)`, removes both `catalog:tenant:slug:{normSlug}` and `catalog:tenant:id:{tenantId:D}` from `_negativeCache` as well as `_hybridCache` so newly provisioned tenants are immediately visible.
3. **Catalog DB Cold-Miss Token Bucket (`MaxCatalogDbMissesPerSecond`):**
   * Before executing `_db.Tenants.AsNoTracking().FirstOrDefaultAsync(...)` on a cache miss, checks an atomic per-second sliding counter (`TryAcquireCatalogDbMissPermit()`).
   * If cold DB misses exceed `MultiTenancyOptions.MaxCatalogDbMissesPerSecond` (default `50` misses/sec), increments `ExamPrismDiagnostics.RecordTenantCatalogShieldRejection()` and returns `null` without querying PostgreSQL (and without caching a false negative, so legitimate retries succeed once the burst subsides).

#### 4.4.2. Strike Integration in `TenantAccessGuardMiddleware`
* When `resolution.Kind == HostResolutionKind.InvalidTenantHost` OR `(resolution.Kind == HostResolutionKind.TenantCandidate && tenantInfo is null)`:
  * Resolves `IIpReputationService` from `context.RequestServices` (if registered) and calls `await reputationService.RecordStrikeAsync(clientIp, IpStrikeReason.UnknownTenantProbe)`.

---

### 4.5. Subsystem 5: Public Heavy Endpoint Rate Limiting & Telemetry

#### 4.5.1. `RateLimitPolicies.TenantOnboarding` (`Common.RateLimiting` & `ExamPrism.Infrastructure.RateLimiting`)
* Adds `public const string TenantOnboarding = "TenantOnboarding";` to `RateLimitPolicies`.
* Adds `SlidingWindowPolicyOptions TenantOnboarding { get; set; } = new(PermitLimit: 5, WindowSeconds: 60, SegmentsPerWindow: 6);` to `RateLimitingOptions`.
* Registers `RateLimitPolicies.TenantOnboarding` in `RateLimitingSetup` using `RedisSlidingWindowRateLimiter` / `InMemorySlidingWindowRateLimiter` partitioned per `platform:onboarding:ip:{ip}`.
* Updates `ProvisionTenantPublicEndpoint` (`POST /api/tenants`) to use `.RequireRateLimiting(RateLimitPolicies.TenantOnboarding)` and `.WithRequestPriority(RequestPriority.Low)`.
* Updates `RateLimitingSetup.OnRejected`:
  * Increments `ExamPrismDiagnostics.RecordRateLimitRejection()`.
  * If `context.HttpContext.User.Identity?.IsAuthenticated != true`, resolves `IIpReputationService` and records `IpStrikeReason.RateLimitExceeded` for the client IP.

#### 4.5.2. Extended DDoS Telemetry (`Common/Telemetry/ExamPrismDiagnostics.cs` & `TelemetrySetup.cs`)
* New counters and snapshot properties in `ExamPrismDiagnostics`:
  * `examprism.security.ip_penalty_box_rejected` (`IpPenaltyBoxRejectedCount`, Prometheus: `examprism_security_ip_penalty_box_rejected_total`)
  * `examprism.security.ip_bans_issued` (`IpBansIssuedCount`, Prometheus: `examprism_security_ip_bans_issued_total`)
  * `examprism.security.decompression_bomb_rejected` (`DecompressionBombRejectedCount`, Prometheus: `examprism_security_decompression_bomb_rejected_total`)
  * `examprism.security.tenant_catalog_shield_rejected` (`TenantCatalogShieldRejectedCount`, Prometheus: `examprism_security_tenant_catalog_shield_rejected_total`)
  * `examprism.security.spoofed_ip_ignored` (`SpoofedIpIgnoredCount`, Prometheus: `examprism_security_spoofed_ip_ignored_total`)
  * `examprism.http.rate_limit_rejected` (`RateLimitRejectedCount`, Prometheus: `examprism_http_rate_limit_rejected_total`)

---

## 5. Updated Middleware Pipeline (`Program.cs`)

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddInfrastructure();

var app = builder.Build();

app.UseAppReverseProxy();                         // 1. Capture OriginalRemoteIp & apply Trusted Proxy ForwardedHeaders
app.UseAppLocalization();                         // 2. Request culture for localized ProblemDetails
app.UseExceptionHandler();                        // 3. Global ProblemDetails exception boundary
app.UseIpPenaltyBox();                            // 4. Zero-latency L1 IP Ban check (with NAT-safe valid-JWT bypass)
app.UseRouting();                                 // 5. Endpoint matching (resolves RequestPriorityMetadata)
app.UseAdaptiveLoadShedding();                    // 6. AIMD / Priority load shedding before decompression or DB work
app.UseRequestTimeouts();                         // 7. Per-endpoint & default timeouts
app.UseRequestDecompression();                    // 8. Decompress incoming gzip/brotli bodies
app.UseDecompressionBombGuard();                  // 9. Enforce MaxDecompressedBodySizeBytes stream read cap
app.UseResponseCompression();                     // 10. Compress outgoing responses
app.UseSerilogRequestLogging(...);                // 11. Structured HTTP logging
app.UseAppCors();                                 // 12. CORS
app.UseMultiTenant();                             // 13. Anti-penetration cached Finbuckle tenant resolution
app.UseMiddleware<TenantAccessGuardMiddleware>(); // 14. Active tenant guard + UnknownTenantProbe strike hook
app.UseAuthentication();                          // 15. JWT auth + Distributed Token Revocation check
app.UseLogEnrichment();                           // 16. Enrich TenantId, UserId, CorrelationId
app.UseRateLimiter();                             // 17. Global + Auth + TenantOnboarding + ExamCritical + Bulkhead
app.UseOutputCache();                             // 18. Distributed Redis / Tenant-scoped output cache
app.UseAuthorization();                           // 19. RBAC policies
app.UseAppTelemetry();                            // 20. /metrics Prometheus endpoint

if (app.Environment.IsDevelopment())
{
    app.MapAppOpenApi();
}

app.MapEndpoints();
```

---

## 6. Verification and Testing Strategy

1. **`ArchitectureTests`:**
   * All new types in `Common.*` and `ExamPrism.Infrastructure.*` must pass 100% of `ArchitectureTests.cs`.
2. **Unit & Integration Tests (`apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs`):**
   * `TrustedProxyIpResolutionTests`: Verify `ResolveClientIp` honors `CF-Connecting-IP`/`X-Real-IP`/`X-Forwarded-For` from trusted private/loopback proxies, ignores spoofed headers from untrusted public IPs while incrementing `SpoofedIpIgnoredCount`, and rejects malformed IP strings.
   * `DecompressionBombGuardTests`: Verify a compressed request body that expands beyond `MaxDecompressedBodySizeBytes` is terminated mid-stream with `413 Payload Too Large` (`DECOMPRESSION_BOMB_DETECTED`), bans the offending IP in `IIpReputationService`, and increments `DecompressionBombRejectedCount`.
   * `IpPenaltyBoxAndNatSafetyTests`: Verify strike accumulation (`UnknownTenantProbe`, `RateLimitExceeded`, `UnauthorizedAuthAttempt`) triggers an IP ban (`429 Too Many Requests` with `X-Penalty-Box: true` and `Retry-After`), and verify **NAT-Safe Valid-JWT Bypass** (a request with a valid signed JWT from the same banned IP succeeds without being blocked).
   * `TenantCatalogPenetrationShieldTests`: Verify invalid slug formats skip `HybridCache` and `CatalogDbContext`, verify unknown tenant misses do not write to `IDistributedCache` (L2 Redis), and verify `MaxCatalogDbMissesPerSecond` protects `CatalogDbContext` during a 200-random-slug flood.
   * `KestrelAndTenantOnboardingDdosTests`: Verify Kestrel `MinRequestBodyDataRate`, `MinResponseDataRate`, header limits, `RateLimitPolicies.TenantOnboarding` on `POST /api/tenants`, and Prometheus `/metrics` exposure of all 6 new DDoS counters.

