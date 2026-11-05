# Design Specification: Load-Balanced Multi-Replica State Coherency & Coordination Hardening

**Date:** 2026-11-05  
**Status:** Draft for Review  
**Scope:** Architectural Subsystem (`Common.*`, `ExamPrism.Infrastructure.*`, `ExamPrism.Endpoints.*`)  

---

## 1. Context and Goals

When ExamPrism runs across $N$ stateless/stateful container replicas behind a Layer-7 Load Balancer (Nginx, Envoy, AWS ALB, Cloudflare, or YARP) using Round-Robin or Least-Connections routing, five subtle multi-replica coordination bugs in the current C# codebase cause state divergence, redundant Redis round-trips, or premature readiness reporting:

1. **Stale L1 Cache on Key Overwrite (`HybridCacheService` & `ICacheBackplane`):**
   * **Problem:** `HybridCacheService.SetAsync` writes updated values to the current replica's L1 memory and shared Redis L2, but never calls `_backplane.PublishKeyInvalidationAsync(scopedKey)`. Peer replicas that previously cached the key in L1 continue serving stale values until `DefaultLocalExpirationMinutes` expires (affecting user-wide JWT revocation timestamps in `HybridCacheTokenRevocationService` and overwritten cache entries).
   * **Solution:** Publish key invalidation on `HybridCacheService.SetAsync` and extend `ICacheBackplane` with in-process invalidation callbacks (`OnKeyInvalidated`, `OnTagInvalidated`) so non-`HybridCache` L1 structures (`TenantCatalogShieldState`, `HybridIpReputationService`, `RedisOutputCacheStore`) also synchronize immediately across replicas.

2. **IP Penalty Box Tenant-Scope Mismatch, L2 Miss Amplification & Round-Robin Strike Dilution (`HybridIpReputationService`):**
   * **Problem 1 (Tenant-Scope Key Mismatch):** `HybridIpReputationService` delegates to `ICacheService` (`HybridCacheService`), which prefixes keys with `t:{tenantId}:` when a tenant is resolved (`RateLimitingSetup.OnRejected` after `UseMultiTenant()`) vs. `platform:` before tenant resolution (`IpPenaltyBoxMiddleware`). Consequently, IP bans triggered on any tenant endpoint are stored under `t:{tenantId}:ddos:ban:ip:{ip}` and are never seen by `IpPenaltyBoxMiddleware` on peer replicas reading `platform:ddos:ban:ip:{ip}`.
   * **Problem 2 (Per-Request Redis L2 Miss):** For unbanned IPs (99.99% of normal traffic), `_localBans.TryGetValue(ip)` misses and `GetBanRemainingAsync` calls `_cache.GetOrDefaultAsync`, triggering a Redis L2 network lookup on every single HTTP request.
   * **Problem 3 (Round-Robin Strike Dilution):** `_strikes` is tracked only in per-replica memory, allowing an attacker routed Round-Robin across $N$ replicas to make $N\times$ more malicious requests before reaching `BanThresholdScore`.
   * **Solution:**
     * Store global IP bans and strikes using tenant-agnostic Redis keys (`{KeyPrefix}ddos:ban:ip:{ip}` and `{KeyPrefix}ddos:strike:ip:{ip}`) via `IConnectionMultiplexer` (when Redis is connected) or `HybridCache` with explicit global keys, while keeping `GetBanRemainingAsync` **100% L1-first** (using a bounded L1 known-clean / backplane-synchronized ban table so normal unbanned traffic performs zero Redis network calls).
     * Synchronize ban/unban events immediately across all replicas via `ICacheBackplane` (`ddos:ban:ip:{ip}`) and accumulate strikes cluster-wide in Redis (`INCRBY` + `PEXPIRE` Lua script with local L1 fallback when Redis is absent or unreachable).

3. **Per-Request Scoped Reset of Anti-Penetration Shield (`CatalogMultiTenantStore` & `TenantCatalogShieldState`):**
   * **Problem:** `CatalogMultiTenantStore` is registered with `ServiceLifetime.Scoped` (one instance per HTTP request), but stores `_negativeCache`, `_missWindowSecond`, and `_missCountInSecond` as instance fields. In production HTTP pipelines, both the L1 negative cache and the `MaxCatalogDbMissesPerSecond` token bucket reset on every single HTTP request. Furthermore, when a tenant is provisioned on Replica A, `RedisCacheBackplane` on Replica B evicts `HybridCache` but never clears Replica B's negative cache.
   * **Solution:** Extract `_negativeCache` and the cold-miss token bucket into a singleton `TenantCatalogShieldState` registered in DI and subscribed to `ICacheBackplane` key/tag invalidations.

4. **Multi-Replica Startup Readiness Race & Synchronous Drain Blocking (`StartupTasksExtensions`, `HostDrainState`, `GracefulShutdownHostedService`):**
   * **Problem 1 (Follower Readiness Race):** When $N$ replicas boot simultaneously, Replica 1 acquires the `"startup:infrastructure-init"` distributed lock and runs DB migrations + seeding. Replicas $2..N$ fail `TryAcquireAsync`, immediately return from `InitializeInfrastructureAsync`, and report `/health/ready = 200 OK` to the Load Balancer before Replica 1 finishes migrating the database.
   * **Problem 2 (Synchronous Drain Sleep):** `GracefulShutdownHostedService.OnStopping` calls `Thread.Sleep` synchronously inside the `ApplicationStopping` callback.
   * **Solution:**
     * Add `IsStartupComplete` (default `true` until startup initialization begins, set `false` while waiting, and `true` once initialization or follower wait completes) to `HostDrainState` and check `!drainState.IsStartupComplete` in `/health/ready`.
     * Follower replicas poll/wait (with configurable bounded timeout and retry interval) for the `"startup:infrastructure-init"` lock to release before completing `InitializeInfrastructureAsync`.
     * Move the graceful shutdown drain delay (`Task.Delay`) into `GracefulShutdownHostedService.StopAsync(CancellationToken)` so shutdown remains non-blocking and cancellation-aware.

5. **Orleans Multi-Container IP Auto-Discovery, OutputCache Prefix/Backplane & Upstream Node Header (`OrleansSetup`, `RedisOutputCacheStore`, `ReverseProxySetup`):**
   * **Orleans Container IP Resolution:** When running in Redis cluster mode without an explicit `Orleans:AdvertisedIpAddress` or `POD_IP`, `OrleansSetup` automatically resolves the container's first non-loopback IPv4 address (`NetworkInterface` / `Dns`) so silos in Docker Compose or bridge networks never advertise `127.0.0.1` to peer containers.
   * **OutputCache Prefix & Fallback Eviction:** `RedisOutputCacheStore` applies `CacheOptions.KeyPrefix` (`{prefix}outputcache:val:{key}` and `{prefix}outputcache:tag:{tag}`) and publishes/subscribes to `ICacheBackplane` tag invalidations so local `_fallbackStore` entries are evicted across all replicas.
   * **Upstream Node Identification (`X-Upstream-Instance`):** `ReverseProxySetup.UseAppReverseProxy` emits a configurable `X-Upstream-Instance` response header (defaulting to `ReverseProxyOptions.InstanceId` or `Environment.MachineName`) so Load Balancers, integration tests, and operators can verify upstream replica distribution and failover.

---

## 2. Architecture & Layering Rules

To maintain 100% compliance with `ArchitectureTests`:
1. **`Common.*` (`apps/backend/src/Common`)**:
   * Houses public contracts and telemetry counters (`ICacheBackplane`, `ExamPrismDiagnostics`).
   * Must **never** reference `ExamPrism.Infrastructure.*`, `ExamPrism.Features.*`, or `ExamPrism.Endpoints.*`.
2. **`ExamPrism.Infrastructure.*` (`apps/backend/src/Infrastructure`)**:
   * Houses all concrete implementations (`HybridCacheService`, `RedisCacheBackplane`, `NoOpCacheBackplane`, `HybridIpReputationService`, `TenantCatalogShieldState`, `CatalogMultiTenantStore`, `RedisOutputCacheStore`, `HostDrainState`, `GracefulShutdownHostedService`, `StartupTasksExtensions`, `OrleansSetup`, `ReverseProxySetup`).
3. **`ExamPrism.Features.*` & `ExamPrism.Endpoints.*`**:
   * Preserve all existing handler and endpoint invariants.

---

## 3. Detailed Subsystem Designs

### 3.1. Subsystem 1: `ICacheBackplane` Invalidation Callbacks & `HybridCacheService.SetAsync` Coherency

#### 3.1.1. `ICacheBackplane` (`Common/Caching/ICacheBackplane.cs`)
```csharp
namespace Common.Caching;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface ICacheBackplane
{
    string InstanceId { get; }

    event Action<string>? KeyInvalidated;
    event Action<string>? TagInvalidated;

    ValueTask PublishKeyInvalidationAsync(string scopedKey, CancellationToken cancellationToken = default);

    ValueTask PublishTagInvalidationAsync(string scopedTag, CancellationToken cancellationToken = default);
}
```

#### 3.1.2. `RedisCacheBackplane` & `NoOpCacheBackplane` (`ExamPrism.Infrastructure.Caching`)
* When `RedisCacheBackplane.OnMessageReceived` processes a message from a peer replica (`senderInstanceId != InstanceId`):
  * For `"K"`: removes `target` from `_hybridCache`, invokes `KeyInvalidated?.Invoke(target)`, and increments `ExamPrismDiagnostics.RecordCacheBackplaneEviction()`.
  * For `"T"`: removes `target` tag from `_hybridCache`, invokes `TagInvalidated?.Invoke(target)`, and increments `ExamPrismDiagnostics.RecordCacheBackplaneEviction()`.
* **`HybridCacheService.SetAsync`:**
  * After `await _hybridCache.SetAsync(scopedKey, value, options, scopedTags, cancellationToken)`, calls `await _backplane.PublishKeyInvalidationAsync(scopedKey, cancellationToken)` so peer replicas immediately evict any stale L1 copy of `scopedKey` and fetch the fresh value from L2 Redis on next read.

---

### 3.2. Subsystem 2: Cluster-Wide Zero-L2-Miss `HybridIpReputationService` (`ExamPrism.Infrastructure.Security`)

#### 3.2.1. Tenant-Agnostic Global Keys & Atomic Strike Script
* `HybridIpReputationService` accepts optional `IConnectionMultiplexer? multiplexer = null`, `ICacheBackplane? backplane = null`, and `IOptions<CacheOptions>? cacheOptions = null` (in addition to existing constructor overloads for backward compatibility with existing unit tests).
* **Global Key Format (never prefixed by tenant scope):**
  * Backplane key notification name: `ddos:ban:ip:{normalizedIp}`
  * Redis ban key: `{keyPrefix}ddos:ban:ip:{normalizedIp}` (stores `expiresAtUnixMs`)
  * Redis strike key: `{keyPrefix}ddos:strike:ip:{normalizedIp}` (atomic Lua script):
    ```lua
    local current = redis.call('INCRBY', KEYS[1], tonumber(ARGV[1]))
    if current == tonumber(ARGV[1]) then
        redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]))
    end
    return current
    ```
* **Zero-Latency L1 `GetBanRemainingAsync(string ipAddress)`:**
  1. Checks `_localBans.TryGetValue(ipAddress, out var expiresAt)`:
     * If `nowMs < expiresAt`, returns `TimeSpan.FromMilliseconds(expiresAt - nowMs)`.
     * If expired, removes from `_localBans`.
  2. Checks `_knownCleanIps` (bounded L1 cache of recently verified unbanned IPs or relies on backplane-synchronized `_localBans` + one-time L2 check per cold IP):
     * When `_checkedIps` (bounded `ConcurrentDictionary<string, long>` with TTL matching `StrikeWindowSeconds`) contains `ipAddress`, returns `null` immediately in **0 network hops** (no Redis L2 call!).
     * On first cold sight of `ipAddress` on a replica (or after `KeyInvalidated` event for `ddos:ban:ip:{ipAddress}` removes `ipAddress` from `_checkedIps`), checks Redis `StringGetAsync("{keyPrefix}ddos:ban:ip:{ip}")` (or global `HybridCache` fallback if `IConnectionMultiplexer` is null):
       * If banned in Redis/L2, populates `_localBans[ipAddress] = cachedExpiry` and returns remaining `TimeSpan`.
       * If not banned, records `ipAddress` in `_checkedIps[ipAddress] = nowMs + windowMs` so subsequent requests from that IP never hit Redis L2!
* **Backplane Synchronization:**
  * Subscribes to `_backplane.KeyInvalidated`:
    * When key starts with `"ddos:ban:ip:"`: extracts `ip` (and optional expiry payload or clears `_checkedIps[ip]` & `_localBans[ip]`) so the replica immediately syncs ban/unban state on the next request without waiting for TTL.
* **Cluster-Wide `RecordStrikeAsync`:**
  * Always updates local `_strikes` and, when `IConnectionMultiplexer` is connected, executes the atomic Redis strike Lua script so Round-Robin requests across $N$ replicas share a single cluster-wide strike score.
  * When `score >= opt.BanThresholdScore`, calls `BanIpAsync`.

---

### 3.3. Subsystem 3: Singleton `TenantCatalogShieldState` (`ExamPrism.Infrastructure.MultiTenancy`)

#### 3.3.1. `TenantCatalogShieldState`
* Registered as a **Singleton** in `MultiTenancySetup.AddMultiTenancy` (`services.TryAddSingleton<TenantCatalogShieldState>()`).
* Holds:
  * `ConcurrentDictionary<string, long> NegativeCache` (case-insensitive)
  * Atomic per-second cold-miss token bucket (`_missWindowSecond`, `_missCountInSecond`)
* Subscribes to `ICacheBackplane.KeyInvalidated` and `ICacheBackplane.TagInvalidated`:
  * When `key` starts with `"catalog:tenant:"`, removes `key` from `NegativeCache`.
  * When `tag` starts with `"catalog:tenant:slug:"`, removes `"catalog:tenant:slug:" + slug` from `NegativeCache`.
* `CatalogMultiTenantStore` injects `TenantCatalogShieldState? shieldState = null` (creating a default instance only when constructed standalone in unit tests without DI, or sharing the injected singleton in DI) so `_negativeCache` and `TryAcquireCatalogDbMissPermit()` persist across all scoped HTTP requests within a replica and synchronize across replicas via the backplane.

---

### 3.4. Subsystem 4: Multi-Replica Startup Readiness Barrier & Async Shutdown Drain

#### 3.4.1. `HostDrainState` & `GetHealthEndpoint` (`ExamPrism.Infrastructure.Health`)
* `HostDrainState` adds:
  * `private volatile bool _isStartupComplete = true;` (defaults to `true` so unit tests that do not run `InitializeInfrastructureAsync` remain ready by default).
  * `public bool IsStartupComplete => _isStartupComplete;`
  * `public bool IsReady => !_isDraining && _isStartupComplete;`
  * `public void MarkStartupIncomplete() => _isStartupComplete = false;`
  * `public void MarkStartupComplete() => _isStartupComplete = true;`
* `GetHealthEndpoint` (`/health/ready` and `/health`):
  * If `drainState.IsDraining` -> returns `503 ServiceUnavailable` with `Status = "Draining"`.
  * If `!drainState.IsStartupComplete` -> returns `503 ServiceUnavailable` with `Status = "Starting"`.

#### 3.4.2. Follower Wait Barrier in `StartupTasksExtensions.InitializeInfrastructureAsync`
* Marks `drainState?.MarkStartupIncomplete()` on entry and guarantees `drainState?.MarkStartupComplete()` in a `finally` block.
* When `distributedLock.TryAcquireAsync("startup:infrastructure-init", TimeSpan.FromMinutes(5), cancellationToken)` returns `null` (another replica holds the startup lock):
  * Instead of returning immediately while the leader is still migrating the DB, polls every `250ms` (up to `GracefulShutdownOptions.StartupWaitTimeoutSeconds`, default `30s`) checking if the lock becomes available or can be briefly acquired/released, then proceeds once the leader releases the lock (or logs a warning on timeout and proceeds).

#### 3.4.3. Non-Blocking Async Drain in `GracefulShutdownHostedService`
* `OnStopping()` only logs and sets `_drainState.MarkDraining()` immediately (zero thread blocking).
* `StopAsync(CancellationToken cancellationToken)` performs `await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken)` (catching `OperationCanceledException` gracefully) when `DrainDelaySeconds > 0`.

---

### 3.5. Subsystem 5: Orleans Container IP Auto-Discovery, `RedisOutputCacheStore` Prefix/Backplane & `X-Upstream-Instance` Header

#### 3.5.1. `OrleansSetup.ResolveContainerIpAddress` (`ExamPrism.Infrastructure.Orleans`)
* Adds `public static IPAddress? ResolveAdvertisedIpAddress(string? configuredIp, string? podIpEnv)`:
  1. Checks `configuredIp`, then `podIpEnv` (`POD_IP`).
  2. If neither is set, inspects `NetworkInterface.GetAllNetworkInterfaces()` for the first operational (`OperationalStatus.Up`), non-loopback (`NetworkInterfaceType != Loopback`) unicast IPv4 address.
* In `OrleansSetup.UseOrleans`: when `isRedisReachable` is true (multi-container cluster mode), applies `ResolveAdvertisedIpAddress` to `EndpointOptions.AdvertisedIPAddress` so container silos never advertise `127.0.0.1` to Redis membership.

#### 3.5.2. `RedisOutputCacheStore` Prefix & Backplane Sync (`ExamPrism.Infrastructure.Http`)
* Prefixes Redis keys with `CacheOptions.KeyPrefix` (`{prefix}outputcache:val:{key}` and `{prefix}outputcache:tag:{tag}`).
* Subscribes to `ICacheBackplane.TagInvalidated` (for `outputcache:tag:{tag}`) and publishes tag invalidation in `EvictByTagAsync` so any replica that fell back to `_fallbackStore` also evicts stale local output cache entries.

#### 3.5.3. `X-Upstream-Instance` Header (`ReverseProxySetup` & `ReverseProxyOptions`)
* `ReverseProxyOptions` adds:
  * `public bool EmitUpstreamInstanceHeader { get; set; } = true;`
  * `public string? InstanceId { get; set; }`
* `ReverseProxySetup.UseAppReverseProxy` sets `context.Response.Headers["X-Upstream-Instance"] = instanceId` (resolved from `ReverseProxyOptions.InstanceId`, `HOSTNAME` / `Environment.MachineName`) when `EmitUpstreamInstanceHeader` is `true`, allowing L7 Load Balancers and multi-node integration tests to inspect which upstream replica handled each request.

---

## 4. Verification and Testing Strategy

1. **`ArchitectureTests`:**
   * Verify 100% pass rate with zero layering violations.
2. **Multi-Replica Load-Balancing Unit & Integration Tests (`apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`):**
   * `HybridCacheService_SetAsync_PublishesBackplaneKeyInvalidation_ToEvictPeerReplicaL1`: Verify overwriting a key on Replica A publishes a backplane invalidation that evicts stale L1 cache on Replica B.
   * `HybridIpReputationService_CrossTenantStrikeAndBan_SynchronizesAcrossReplicas_WithZeroL2HitsOnCleanTraffic`: Verify strikes recorded inside a tenant context on Replica A ban the IP globally on Replica B's `IpPenaltyBoxMiddleware` (before `UseMultiTenant`), verify round-robin strikes across Replica A and Replica B accumulate toward a single threshold in Redis, and verify repeated requests from a clean IP do not call L2 cache after the initial check.
   * `CatalogMultiTenantStore_ScopedInstances_ShareSingletonShieldState_AndEvictNegativeCacheViaBackplane`: Verify two separate DI scopes share `_negativeCache` and `MaxCatalogDbMissesPerSecond`, and verify backplane invalidation from Replica A clears Replica B's negative cache immediately.
   * `StartupTasks_FollowerReplica_WaitsForLeaderLockRelease_AndHealthReadyReflectsStartupAndDrainState`: Verify follower replica waits for leader migration lock before marking startup complete, `/health/ready` returns `503` while `!IsStartupComplete`, and `GracefulShutdownHostedService.StopAsync` awaits drain asynchronously.
   * `OrleansAndReverseProxy_AutoDetectsNonLoopbackIp_AndEmitsUpstreamInstanceHeader`: Verify `OrleansSetup.ResolveAdvertisedIpAddress` selects a valid non-loopback IPv4 address, `RedisOutputCacheStore` prefixes keys and syncs fallback tag eviction via backplane, and `UseAppReverseProxy` emits `X-Upstream-Instance`.
