# Load-Balanced Multi-Replica State Coherency & Coordination Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate cross-replica L1 cache staleness, tenant-scoped IP ban mismatches, per-request anti-penetration shield resets, follower startup readiness races, and container IP/header blind spots when running $N$ ExamPrism replicas behind a Load Balancer.

**Architecture:** Extends `ICacheBackplane` with in-process invalidation events (`KeyInvalidated`, `TagInvalidated`) and publishes key invalidations on `HybridCacheService.SetAsync`. Upgrades `HybridIpReputationService` to use global tenant-agnostic keys, bounded L1 clean-IP caching (zero L2 network hops on normal traffic), and atomic Redis strike accumulation; extracts `TenantCatalogShieldState` as a backplane-aware singleton; adds a follower startup readiness wait-barrier in `StartupTasksExtensions` + async drain in `GracefulShutdownHostedService`; and adds container IPv4 auto-discovery in `OrleansSetup` plus `X-Upstream-Instance` headers in `ReverseProxySetup`.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, HybridCache, StackExchange.Redis, Finbuckle.MultiTenant, Microsoft Orleans, xUnit, FluentAssertions, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-05-load-balancing-multi-replica-coherency-design.md`

## Global Constraints

- `Common.*` (`apps/backend/src/Common`) must never reference `ExamPrism.Infrastructure.*`, `ExamPrism.Features.*`, or `ExamPrism.Endpoints.*` (`ArchitectureTests.TypesInCommon_MustNotReferenceInfrastructureFeaturesOrEndpoints`).
- `ExamPrism.Features.*` handlers must remain `public sealed`, end with `Handler`, use non-defaulted constructors, and never reference ASP.NET Core HTTP types or `ExamPrism.Infrastructure` interfaces.
- `ExamPrism.Endpoints.*` endpoints must remain `public sealed`, implement `IEndpoint`, execute inline in `MapEndpoint`, and never inject `DbContext`.
- Existing constructor signatures on `HybridIpReputationService`, `CatalogMultiTenantStore`, `RedisOutputCacheStore`, and `HybridCacheService` must remain backward-compatible with all existing unit and integration tests.

## Review Focus

- `RedisCacheBackplane.OnMessageReceived` subscriber exception isolation: an exception thrown by one `KeyInvalidated` or `TagInvalidated` event listener must not prevent `_hybridCache` eviction or crash the Redis Pub/Sub callback thread.
- `HybridIpReputationService` tenant-context immunity: recording strikes or bans while `IMultiTenantContextAccessor` holds an active tenant (`t:{id}:`) must still write and read the exact same global key (`ddos:ban:ip:{ip}`) checked by `IpPenaltyBoxMiddleware` before `UseMultiTenant()`.
- `HybridIpReputationService` L1-first clean traffic guarantee: after the initial check for an unbanned IP, subsequent calls to `GetBanRemainingAsync` within the strike window must return `null` from L1 without invoking Redis or `ICacheService.GetOrDefaultAsync` until a backplane invalidation arrives for `ddos:ban:ip:{ip}`.
- `StartupTasksExtensions.InitializeInfrastructureAsync` follower timeout safety: if a crashed leader holds `"startup:infrastructure-init"` beyond `StartupWaitTimeoutSeconds`, the follower replica must log a warning, mark `HostDrainState.MarkStartupComplete()`, and not deadlock startup indefinitely.
- `OrleansSetup.ResolveAdvertisedIpAddress` invalid/loopback filtering: must ignore `"127.0.0.1"`, `"::1"`, or malformed IP strings in `AdvertisedIpAddress` / `POD_IP` when falling back to operational non-loopback IPv4 interfaces.

---

### Task 1: `ICacheBackplane` Invalidation Events & `HybridCacheService.SetAsync` Coherency

**Files:**
- Modify: `apps/backend/src/Common/Caching/ICacheBackplane.cs:6-13`
- Modify: `apps/backend/src/Common/Caching/NoOpCacheBackplane.cs:6-15`
- Modify: `apps/backend/src/Infrastructure/Caching/RedisCacheBackplane.cs:14-150`
- Modify: `apps/backend/src/Infrastructure/Caching/HybridCacheService.cs:76-97`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`

**Interfaces:**
- Consumes: `HybridCache`, `IConnectionMultiplexer`, `ExamPrismDiagnostics.RecordCacheBackplaneEviction()`
- Produces:
  - `ICacheBackplane.KeyInvalidated` (`event Action<string>?`)
  - `ICacheBackplane.TagInvalidated` (`event Action<string>?`)
  - `HybridCacheService.SetAsync` publishing `_backplane.PublishKeyInvalidationAsync(scopedKey, cancellationToken)`

- [ ] **Step 1: Write the failing tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`**

```csharp
namespace ExamPrism.Tests.Infrastructure;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Common.Caching;
using ExamPrism.Infrastructure.Caching;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

public class LoadBalancingMultiReplicaTests
{
    [Fact]
    public async Task HybridCacheService_SetAsync_PublishesBackplaneKeyInvalidation_ToEvictPeerReplicaL1()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        using var sp = services.BuildServiceProvider();
        var hybridCache = sp.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        var backplane = Substitute.For<ICacheBackplane>();
        var sut = new HybridCacheService(
            hybridCache,
            Options.Create(new CacheOptions()),
            backplane);

        await sut.SetAsync("auth:revoked:user:platform:u1", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));

        await backplane.Received(1).PublishKeyInvalidationAsync(
            "platform:auth:revoked:user:platform:u1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RedisCacheBackplane_OnPeerMessage_RaisesKeyAndTagInvalidatedEvents_AndIsolatesListenerExceptions()
    {
        Action<RedisChannel, RedisValue>? capturedHandler = null;
        var subscriber = Substitute.For<ISubscriber>();
        subscriber.SubscribeAsync(
            Arg.Any<RedisChannel>(),
            Arg.Do<Action<RedisChannel, RedisValue>>(h => capturedHandler = h),
            Arg.Any<CommandFlags>())
            .Returns(Task.CompletedTask);

        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);

        var services = new ServiceCollection();
        services.AddHybridCache();
        using var sp = services.BuildServiceProvider();
        var hybridCache = sp.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();

        using var backplane = new RedisCacheBackplane(
            mux,
            hybridCache,
            Options.Create(new CacheOptions { KeyPrefix = "examprism:", BackplaneChannelName = "cache:backplane" }),
            NullLogger<RedisCacheBackplane>.Instance);

        await backplane.StartAsync(CancellationToken.None);
        capturedHandler.Should().NotBeNull();

        var invalidatedKeys = new List<string>();
        var invalidatedTags = new List<string>();
        backplane.KeyInvalidated += _ => throw new InvalidOperationException("listener failure");
        backplane.KeyInvalidated += k => invalidatedKeys.Add(k);
        backplane.TagInvalidated += t => invalidatedTags.Add(t);

        capturedHandler!(RedisChannel.Literal("examprism:cache:backplane"), "peerNode|K|ddos:ban:ip:1.2.3.4");
        capturedHandler!(RedisChannel.Literal("examprism:cache:backplane"), "peerNode|T|catalog:tenant:slug:acme");

        invalidatedKeys.Should().ContainSingle().Which.Should().Be("ddos:ban:ip:1.2.3.4");
        invalidatedTags.Should().ContainSingle().Which.Should().Be("catalog:tenant:slug:acme");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~LoadBalancingMultiReplicaTests"`
Expected: FAIL with compilation error (`ICacheBackplane` does not contain definition for `KeyInvalidated` / `TagInvalidated`).

- [ ] **Step 3: Implement `KeyInvalidated` & `TagInvalidated` on `ICacheBackplane`, `NoOpCacheBackplane`, `RedisCacheBackplane`, and publish key invalidation in `HybridCacheService.SetAsync`**

In `apps/backend/src/Common/Caching/ICacheBackplane.cs` and `NoOpCacheBackplane.cs`, add `event Action<string>? KeyInvalidated;` and `event Action<string>? TagInvalidated;`.
In `apps/backend/src/Infrastructure/Caching/RedisCacheBackplane.cs`, after `_hybridCache.RemoveAsync(target)` / `RemoveByTagAsync(target)`, iterate through `KeyInvalidated?.GetInvocationList()` / `TagInvalidated?.GetInvocationList()` wrapping each delegate invocation in a `try/catch` so a throwing listener logs a warning without aborting remaining listeners or the backplane callback.
In `apps/backend/src/Infrastructure/Caching/HybridCacheService.cs`, call `await _backplane.PublishKeyInvalidationAsync(scopedKey, cancellationToken);` at the end of `SetAsync`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~LoadBalancingMultiReplicaTests|FullyQualifiedName~HybridCacheServiceTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Caching/ICacheBackplane.cs apps/backend/src/Common/Caching/NoOpCacheBackplane.cs apps/backend/src/Infrastructure/Caching/RedisCacheBackplane.cs apps/backend/src/Infrastructure/Caching/HybridCacheService.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs
git commit -m "fix(caching): publish backplane key invalidation on SetAsync and expose invalidation events"
```

---

### Task 2: Global Zero-L2-Miss `HybridIpReputationService` & Cluster-Wide Strike Sync

**Files:**
- Modify: `apps/backend/src/Infrastructure/Security/HybridIpReputationService.cs:12-183`
- Modify: `apps/backend/src/Infrastructure/Security/DdosProtectionSetup.cs:21-28`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`

**Interfaces:**
- Consumes: `ICacheBackplane.KeyInvalidated`, `ICacheBackplane.PublishKeyInvalidationAsync`, `IConnectionMultiplexer`, `ICacheService`, `IOptions<CacheOptions>`
- Produces:
  - `HybridIpReputationService(IOptions<DdosProtectionOptions> options, ICacheService? cache = null, TimeProvider? timeProvider = null, IConnectionMultiplexer? multiplexer = null, ICacheBackplane? backplane = null, IOptions<CacheOptions>? cacheOptions = null)`

- [ ] **Step 1: Add failing tests for cross-tenant IP ban, zero L2 hits on clean traffic, and cluster-wide strikes in `apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`**

```csharp
    [Fact]
    public async Task HybridIpReputationService_CrossTenantBan_AndCleanTraffic_ZeroRepeatedL2Calls_AndBackplaneSync()
    {
        var ddosOpts = Options.Create(new ExamPrism.Infrastructure.Security.DdosProtectionOptions
        {
            Enabled = true,
            BanThresholdScore = 30,
            UnknownTenantProbeWeight = 20,
            StrikeWindowSeconds = 60,
            BanDurationSeconds = 300
        });
        var cacheOpts = Options.Create(new CacheOptions { KeyPrefix = "examprism:" });

        var redisStore = new Dictionary<string, RedisValue>(StringComparer.Ordinal);
        var db = Substitute.For<IDatabase>();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                redisStore[ci.ArgAt<RedisKey>(0).ToString()] = ci.ArgAt<RedisValue>(1);
                return Task.FromResult(true);
            });
        var stringGetCalls = 0;
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                Interlocked.Increment(ref stringGetCalls);
                var k = ci.ArgAt<RedisKey>(0).ToString();
                return Task.FromResult(redisStore.TryGetValue(k, out var v) ? v : RedisValue.Null);
            });
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci => Task.FromResult(redisStore.Remove(ci.ArgAt<RedisKey>(0).ToString())));
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]?>(), Arg.Any<RedisValue[]?>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                var k = ci.ArgAt<RedisKey[]>(1)[0].ToString();
                var delta = (long)ci.ArgAt<RedisValue[]>(2)[0];
                var cur = redisStore.TryGetValue(k, out var existing) && existing.TryParse(out long parsed) ? parsed : 0L;
                var next = cur + delta;
                redisStore[k] = next;
                return Task.FromResult(RedisResult.Create((RedisValue)next));
            });

        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.IsConnected.Returns(true);
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var backplaneA = Substitute.For<ICacheBackplane>();
        var backplaneB = Substitute.For<ICacheBackplane>();
        backplaneA.When(b => b.PublishKeyInvalidationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(ci => backplaneB.KeyInvalidated += Raise.Event<Action<string>>(ci.ArgAt<string>(0)));

        var replicaA = new ExamPrism.Infrastructure.Security.HybridIpReputationService(
            ddosOpts, cache: null, TimeProvider.System, mux, backplaneA, cacheOpts);
        var replicaB = new ExamPrism.Infrastructure.Security.HybridIpReputationService(
            ddosOpts, cache: null, TimeProvider.System, mux, backplaneB, cacheOpts);

        // 1. Clean IP checked 5 times on Replica B -> only 1 L2 Redis lookup, then cached clean in L1
        for (var i = 0; i < 5; i++)
        {
            (await replicaB.GetBanRemainingAsync("203.0.113.50")).Should().BeNull();
        }
        stringGetCalls.Should().Be(1);

        // 2. Round-robin strikes: 1st strike on Replica A (score 20 < 30), 2nd strike on Replica A or B (score 40 >= 30 -> banned!)
        await replicaB.RecordStrikeAsync("203.0.113.50", Common.Security.IpStrikeReason.UnknownTenantProbe);
        (await replicaB.GetBanRemainingAsync("203.0.113.50")).Should().BeNull();

        await replicaA.RecordStrikeAsync("203.0.113.50", Common.Security.IpStrikeReason.UnknownTenantProbe);

        // 3. Replica B immediately sees the ban via backplane invalidation of _checkedIps + shared Redis ban key
        var banOnB = await replicaB.GetBanRemainingAsync("203.0.113.50");
        banOnB.Should().NotBeNull();
        banOnB!.Value.TotalSeconds.Should().BeGreaterThan(200);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~HybridIpReputationService_CrossTenantBan"`
Expected: FAIL with constructor argument mismatch on `HybridIpReputationService`.

- [ ] **Step 3: Implement global Redis + L1 `_checkedIps` + backplane sync in `HybridIpReputationService` and wire in `DdosProtectionSetup`**

In `apps/backend/src/Infrastructure/Security/HybridIpReputationService.cs`:
- Maintain `_checkedIps = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase)` (stores `ip -> checkedUntilUnixMs` for verified clean IPs, bounded by `MaxTrackedIps`).
- Subscribe to `backplane.KeyInvalidated`: when `key` starts with `"ddos:ban:ip:"`, extract `ip = key["ddos:ban:ip:".Length..]`, remove `ip` from `_checkedIps` and `_localBans` so the next call re-reads the updated state from L2/Redis.
- In `GetBanRemainingAsync`:
  1. Check `_localBans` first.
  2. Check `_checkedIps`: if `nowMs < checkedUntil`, return `null` immediately (0 L2/Redis calls).
  3. Query Redis `db.StringGetAsync($"{_keyPrefix}ddos:ban:ip:{ipAddress}")` when `_multiplexer is { IsConnected: true }`, falling back to `_cache.GetOrDefaultAsync<long?>($"ddos:ban:ip:{ipAddress}")` when `_cache` is provided.
  4. If not banned in L2/Redis, set `_checkedIps[ipAddress] = nowMs + (_options.Value.StrikeWindowSeconds * 1000L)` and return `null`.
- In `RecordStrikeAsync`:
  - Update local `_strikes`, and if `_multiplexer is { IsConnected: true }`, execute the atomic Lua script (`INCRBY` + `PEXPIRE`) on `"{_keyPrefix}ddos:strike:ip:{ipAddress}"` and use `Math.Max(newState.Score, (int)redisScore)`.
- In `BanIpAsync` and `UnbanIpAsync`:
  - Remove `ipAddress` from `_checkedIps`, write/delete `"{_keyPrefix}ddos:ban:ip:{ipAddress}"` in Redis (and `_cache` if present), and call `await _backplane.PublishKeyInvalidationAsync($"ddos:ban:ip:{ipAddress}", cancellationToken)`.
- Update `apps/backend/src/Infrastructure/Security/DdosProtectionSetup.cs` to pass `sp.GetService<IConnectionMultiplexer>()`, `sp.GetService<ICacheBackplane>()`, and `sp.GetService<IOptions<CacheOptions>>()` into `HybridIpReputationService`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~LoadBalancingMultiReplicaTests|FullyQualifiedName~DdosProtectionSuiteTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/Security/HybridIpReputationService.cs apps/backend/src/Infrastructure/Security/DdosProtectionSetup.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs
git commit -m "fix(security): make HybridIpReputationService L1-first with global Redis strikes and backplane sync"
```

---

### Task 3: Singleton `TenantCatalogShieldState` & Cross-Replica Negative-Cache Eviction

**Files:**
- Create: `apps/backend/src/Infrastructure/MultiTenancy/TenantCatalogShieldState.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs:26-129,447-478`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/MultiTenancySetup.cs:87-93`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`

**Interfaces:**
- Consumes: `ICacheBackplane.KeyInvalidated`, `ICacheBackplane.TagInvalidated`, `IOptions<MultiTenancyOptions>`, `TimeProvider`
- Produces:
  - `public sealed class TenantCatalogShieldState` (`IsNegativelyCached`, `RecordNegativeCache`, `RemoveNegativeCache`, `TryAcquireCatalogDbMissPermit`)
  - `CatalogMultiTenantStore` constructor accepting optional `TenantCatalogShieldState? shieldState = null`

- [ ] **Step 1: Add failing test for scoped `CatalogMultiTenantStore` instances sharing `TenantCatalogShieldState` and backplane eviction in `apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`**

```csharp
    [Fact]
    public async Task CatalogMultiTenantStore_ScopedInstances_ShareSingletonShieldState_AndEvictNegativeCacheViaBackplane()
    {
        var mtOpts = Options.Create(new ExamPrism.Infrastructure.MultiTenancy.MultiTenancyOptions
        {
            MaxCatalogDbMissesPerSecond = 2,
            NotFoundCacheExpirationSeconds = 60,
            MaxNegativeCacheEntries = 100
        });

        var backplane = Substitute.For<ICacheBackplane>();
        var shieldState = new ExamPrism.Infrastructure.MultiTenancy.TenantCatalogShieldState(mtOpts, backplane, TimeProvider.System);

        shieldState.RecordNegativeCache("catalog:tenant:slug:new-school", TimeSpan.FromSeconds(60));
        shieldState.IsNegativelyCached("catalog:tenant:slug:new-school").Should().BeTrue();

        // Simulate peer replica publishing key invalidation when "new-school" is provisioned
        backplane.KeyInvalidated += Raise.Event<Action<string>>("catalog:tenant:slug:new-school");
        shieldState.IsNegativelyCached("catalog:tenant:slug:new-school").Should().BeFalse();

        // Token bucket is shared across calls on the singleton state
        shieldState.TryAcquireCatalogDbMissPermit().Should().BeTrue();
        shieldState.TryAcquireCatalogDbMissPermit().Should().BeTrue();
        shieldState.TryAcquireCatalogDbMissPermit().Should().BeFalse();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~CatalogMultiTenantStore_ScopedInstances"`
Expected: FAIL with compilation error (`TenantCatalogShieldState` does not exist).

- [ ] **Step 3: Create `TenantCatalogShieldState`, inject into `CatalogMultiTenantStore`, and register singleton in `MultiTenancySetup`**

Create `apps/backend/src/Infrastructure/MultiTenancy/TenantCatalogShieldState.cs` holding `_negativeCache`, `_missWindowSecond`, `_missCountInSecond`, and subscribing to `backplane.KeyInvalidated` (removing matching key) and `backplane.TagInvalidated` (when `tag.StartsWith("catalog:tenant:slug:")`, removing `$"catalog:tenant:slug:{tag["catalog:tenant:slug:".Length..]}"`).
Update `CatalogMultiTenantStore` to accept `TenantCatalogShieldState? shieldState = null` (defaulting to `new TenantCatalogShieldState(options, backplane, timeProvider)` when null) and delegate `IsNegativelyCached`, `RecordNegativeCache`, `_shieldState.RemoveNegativeCache(key)`, and `TryAcquireCatalogDbMissPermit`.
Register `services.TryAddSingleton<TenantCatalogShieldState>();` in `MultiTenancySetup.AddMultiTenancy`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~LoadBalancingMultiReplicaTests|FullyQualifiedName~DdosProtectionSuiteTests|FullyQualifiedName~SubdomainAndCatalogStoreTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/MultiTenancy/TenantCatalogShieldState.cs apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs apps/backend/src/Infrastructure/MultiTenancy/MultiTenancySetup.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs
git commit -m "fix(multitenancy): extract singleton TenantCatalogShieldState with backplane negative-cache eviction"
```

---

### Task 4: Multi-Replica Startup Readiness Barrier & Async Shutdown Drain

**Files:**
- Modify: `apps/backend/src/Infrastructure/Health/HostDrainState.cs:3-18`
- Modify: `apps/backend/src/Infrastructure/Health/GracefulShutdownOptions.cs:3-8`
- Modify: `apps/backend/src/Infrastructure/Health/GracefulShutdownHostedService.cs:8-51`
- Modify: `apps/backend/src/Endpoints/Health/GetHealthEndpoint.cs:24-47`
- Modify: `apps/backend/src/Infrastructure/StartupTasksExtensions.cs:10-39`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`

**Interfaces:**
- Consumes: `IDistributedLock`, `HostDrainState`, `GracefulShutdownOptions`
- Produces:
  - `HostDrainState.IsStartupComplete` (`bool`), `HostDrainState.IsReady` (`bool`), `MarkStartupIncomplete()`, `MarkStartupComplete()`
  - `GracefulShutdownOptions.StartupWaitTimeoutSeconds` (`int`, default `30`), `StartupPollIntervalMs` (`int`, default `200`)

- [ ] **Step 1: Add failing tests for follower startup wait barrier, `/health/ready` startup check, and async drain in `apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`**

```csharp
    [Fact]
    public async Task HostDrainState_And_GracefulShutdown_TrackStartupReadiness_AndAwaitDrainAsynchronously()
    {
        var drainState = new ExamPrism.Infrastructure.Health.HostDrainState();
        drainState.IsStartupComplete.Should().BeTrue();
        drainState.IsReady.Should().BeTrue();

        drainState.MarkStartupIncomplete();
        drainState.IsStartupComplete.Should().BeFalse();
        drainState.IsReady.Should().BeFalse();

        drainState.MarkStartupComplete();
        drainState.IsReady.Should().BeTrue();

        using var lifetimeCts = new CancellationTokenSource();
        var lifetime = Substitute.For<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(lifetimeCts.Token);

        var opts = Options.Create(new ExamPrism.Infrastructure.Health.GracefulShutdownOptions
        {
            DrainDelaySeconds = 1,
            StartupWaitTimeoutSeconds = 2,
            StartupPollIntervalMs = 50
        });

        var svc = new ExamPrism.Infrastructure.Health.GracefulShutdownHostedService(
            drainState,
            lifetime,
            opts,
            NullLogger<ExamPrism.Infrastructure.Health.GracefulShutdownHostedService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        lifetimeCts.Cancel();
        drainState.IsDraining.Should().BeTrue();
        drainState.IsReady.Should().BeFalse();

        using var stopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await svc.Invoking(s => s.StopAsync(stopCts.Token)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartupTasks_WaitForLeaderAsync_PollsUntilLeaderReleasesLock_OrTimesOutGracefully()
    {
        var attempts = 0;
        var distributedLock = Substitute.For<Common.Locking.IDistributedLock>();
        var handle = Substitute.For<Common.Locking.IDistributedLockHandle>();
        distributedLock.TryAcquireAsync("startup:infrastructure-init", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var cur = Interlocked.Increment(ref attempts);
                return ValueTask.FromResult<Common.Locking.IDistributedLockHandle?>(cur >= 3 ? handle : null);
            });

        var released = await ExamPrism.Infrastructure.StartupTasksExtensions.WaitForLeaderStartupAsync(
            distributedLock,
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(20),
            NullLogger.Instance,
            CancellationToken.None);

        released.Should().BeTrue();
        attempts.Should().BeGreaterThanOrEqualTo(3);
        await handle.Received(1).DisposeAsync();
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~HostDrainState_And_GracefulShutdown|FullyQualifiedName~StartupTasks_WaitForLeaderAsync"`
Expected: FAIL with compilation error (`IsStartupComplete`, `IsReady`, `WaitForLeaderStartupAsync` not defined).

- [ ] **Step 3: Implement `HostDrainState` readiness flags, async `GracefulShutdownHostedService.StopAsync`, `/health/ready` check, and `StartupTasksExtensions.WaitForLeaderStartupAsync`**

- In `apps/backend/src/Infrastructure/Health/HostDrainState.cs`: add `private volatile bool _isStartupComplete = true;`, `public bool IsStartupComplete => _isStartupComplete;`, `public bool IsReady => !_isDraining && _isStartupComplete;`, `MarkStartupIncomplete()`, `MarkStartupComplete()`, and reset `_isStartupComplete = true` in `Reset()`.
- In `apps/backend/src/Infrastructure/Health/GracefulShutdownOptions.cs`: add `public int StartupWaitTimeoutSeconds { get; set; } = 30;` and `public int StartupPollIntervalMs { get; set; } = 200;`.
- In `apps/backend/src/Infrastructure/Health/GracefulShutdownHostedService.cs`: remove `Thread.Sleep` from `OnStopping()`; in `StopAsync(CancellationToken cancellationToken)`, mark `_drainState.MarkDraining()` and if `_options.Value.DrainDelaySeconds > 0`, `await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken)` inside a `try/catch (OperationCanceledException)`.
- In `apps/backend/src/Endpoints/Health/GetHealthEndpoint.cs`: after checking `if (drainState.IsDraining)`, check `if (!drainState.IsStartupComplete)` and return `Results.Json(new GetHealthResult("Starting", DateTime.UtcNow, false), statusCode: StatusCodes.Status503ServiceUnavailable);`.
- In `apps/backend/src/Infrastructure/StartupTasksExtensions.cs`: wrap `InitializeInfrastructureAsync` with `drainState?.MarkStartupIncomplete()` and `finally { drainState?.MarkStartupComplete(); }`. When `handle is null`, call `await WaitForLeaderStartupAsync(distributedLock, timeout, pollInterval, logger, cancellationToken)` which polls `TryAcquireAsync` until it acquires (and immediately disposes) the lock or times out with a warning.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~LoadBalancingMultiReplicaTests|FullyQualifiedName~EdgeAndHealthTests|FullyQualifiedName~HealthEndpointTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/Health/HostDrainState.cs apps/backend/src/Infrastructure/Health/GracefulShutdownOptions.cs apps/backend/src/Infrastructure/Health/GracefulShutdownHostedService.cs apps/backend/src/Endpoints/Health/GetHealthEndpoint.cs apps/backend/src/Infrastructure/StartupTasksExtensions.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs
git commit -m "fix(health): add follower startup readiness wait barrier and non-blocking shutdown drain"
```

---

### Task 5: Orleans Container IP Auto-Discovery, `RedisOutputCacheStore` Prefix/Backplane & `X-Upstream-Instance` Header

**Files:**
- Modify: `apps/backend/src/Infrastructure/Orleans/OrleansSetup.cs:111-121,166-176`
- Modify: `apps/backend/src/Infrastructure/Http/RedisOutputCacheStore.cs:9-136`
- Modify: `apps/backend/src/Infrastructure/Http/HttpPerformanceSetup.cs:70-75`
- Modify: `apps/backend/src/Infrastructure/Http/ReverseProxyOptions.cs:3-12`
- Modify: `apps/backend/src/Infrastructure/Http/ReverseProxySetup.cs:71-86`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`

**Interfaces:**
- Consumes: `ICacheBackplane`, `IOptions<CacheOptions>`, `ReverseProxyOptions`
- Produces:
  - `OrleansSetup.ResolveAdvertisedIpAddress(string? configuredIp, string? podIpEnv)` (`IPAddress?`)
  - `RedisOutputCacheStore(IConnectionMultiplexer? connectionMultiplexer = null, IOutputCacheStore? fallbackStore = null, ILogger<RedisOutputCacheStore>? logger = null, ICacheBackplane? backplane = null, string? keyPrefix = null)`
  - `ReverseProxyOptions.EmitUpstreamInstanceHeader` (`bool`, default `true`), `ReverseProxyOptions.InstanceId` (`string?`)

- [ ] **Step 1: Add failing tests for `ResolveAdvertisedIpAddress`, `RedisOutputCacheStore` prefix & backplane fallback eviction, and `X-Upstream-Instance` header in `apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs`**

```csharp
    [Fact]
    public async Task OrleansIpResolution_OutputCacheBackplane_And_UpstreamHeader_WorkAcrossReplicas()
    {
        // 1. Orleans IP resolution ignores loopback/invalid strings and resolves explicit or host non-loopback IPv4
        var explicitIp = ExamPrism.Infrastructure.Orleans.OrleansSetup.ResolveAdvertisedIpAddress("10.244.1.15", null);
        explicitIp.Should().Be(System.Net.IPAddress.Parse("10.244.1.15"));

        var loopbackFallback = ExamPrism.Infrastructure.Orleans.OrleansSetup.ResolveAdvertisedIpAddress("127.0.0.1", "invalid-ip");
        if (loopbackFallback is not null)
        {
            System.Net.IPAddress.IsLoopback(loopbackFallback).Should().BeFalse();
        }

        // 2. RedisOutputCacheStore prefixes keys and evicts peer fallback store via backplane
        var backplaneA = Substitute.For<ICacheBackplane>();
        var backplaneB = Substitute.For<ICacheBackplane>();
        backplaneA.When(b => b.PublishTagInvalidationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(ci => backplaneB.TagInvalidated += Raise.Event<Action<string>>(ci.ArgAt<string>(0)));

        var storeA = new ExamPrism.Infrastructure.Http.RedisOutputCacheStore(
            connectionMultiplexer: null, fallbackStore: null, logger: null, backplane: backplaneA, keyPrefix: "examprism:");
        var storeB = new ExamPrism.Infrastructure.Http.RedisOutputCacheStore(
            connectionMultiplexer: null, fallbackStore: null, logger: null, backplane: backplaneB, keyPrefix: "examprism:");

        await storeB.SetAsync("page1", [1, 2, 3], ["tenant-1"], TimeSpan.FromMinutes(5), CancellationToken.None);
        (await storeB.GetAsync("page1", CancellationToken.None)).Should().NotBeNull();

        await storeA.EvictByTagAsync("tenant-1", CancellationToken.None);
        (await storeB.GetAsync("page1", CancellationToken.None)).Should().BeNull();

        // 3. UseAppReverseProxy emits X-Upstream-Instance header
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var services = new ServiceCollection();
        services.Configure<ExamPrism.Infrastructure.Http.ReverseProxyOptions>(o =>
        {
            o.EmitUpstreamInstanceHeader = true;
            o.InstanceId = "replica-node-2";
        });
        ctx.RequestServices = services.BuildServiceProvider();

        var appBuilder = new Microsoft.AspNetCore.Builder.ApplicationBuilder(ctx.RequestServices);
        ExamPrism.Infrastructure.Http.ReverseProxySetup.UseAppReverseProxy(appBuilder);
        appBuilder.Run(_ => Task.CompletedTask);
        var pipeline = appBuilder.Build();

        await pipeline(ctx);
        ctx.Response.Headers["X-Upstream-Instance"].ToString().Should().Be("replica-node-2");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~OrleansIpResolution_OutputCacheBackplane"`
Expected: FAIL with compilation error (`ResolveAdvertisedIpAddress`, `EmitUpstreamInstanceHeader`, `InstanceId` not defined).

- [ ] **Step 3: Implement `OrleansSetup.ResolveAdvertisedIpAddress`, `RedisOutputCacheStore` prefix & backplane sync, and `X-Upstream-Instance` in `ReverseProxySetup`**

- In `apps/backend/src/Infrastructure/Orleans/OrleansSetup.cs`:
  - Add `public static IPAddress? ResolveAdvertisedIpAddress(string? configuredIp, string? podIpEnv)` which checks `configuredIp`, then `podIpEnv` (accepting only valid non-loopback `IPAddress`), and otherwise enumerates `NetworkInterface.GetAllNetworkInterfaces()` for an `OperationalStatus.Up`, non-loopback `AddressFamily.InterNetwork` unicast IP.
  - Call `ResolveAdvertisedIpAddress` in `UseOrleans` when setting `EndpointOptions.AdvertisedIPAddress`.
- In `apps/backend/src/Infrastructure/Http/RedisOutputCacheStore.cs`:
  - Add optional constructor parameters `ICacheBackplane? backplane = null, string? keyPrefix = null`.
  - Prefix Redis keys as `$"{_keyPrefix}outputcache:val:{key}"` and `$"{_keyPrefix}outputcache:tag:{tag}"`.
  - Subscribe to `_backplane.TagInvalidated`: when `scopedTag` starts with `"outputcache:tag:"`, call `_fallbackStore.EvictByTagAsync(scopedTag["outputcache:tag:".Length..], CancellationToken.None)`.
  - In `EvictByTagAsync(string tag, CancellationToken cancellationToken)`: after evicting from Redis and `_fallbackStore`, call `await _backplane.PublishTagInvalidationAsync($"outputcache:tag:{tag}", cancellationToken)` when `_backplane is not null`.
  - Update `HttpPerformanceSetup.AddAppHttpPerformance` to pass `sp.GetService<ICacheBackplane>()` and `sp.GetService<IOptions<CacheOptions>>()?.Value.KeyPrefix` into `RedisOutputCacheStore`.
- In `apps/backend/src/Infrastructure/Http/ReverseProxyOptions.cs` & `ReverseProxySetup.cs`:
  - Add `public bool EmitUpstreamInstanceHeader { get; set; } = true;` and `public string? InstanceId { get; set; }` to `ReverseProxyOptions`.
  - In `ReverseProxySetup.UseAppReverseProxy`, resolve `proxyOpts = context.RequestServices?.GetService<IOptions<ReverseProxyOptions>>()?.Value` and when `proxyOpts?.EmitUpstreamInstanceHeader ?? true`, set `context.Response.Headers["X-Upstream-Instance"] = !string.IsNullOrWhiteSpace(proxyOpts?.InstanceId) ? proxyOpts!.InstanceId : Environment.MachineName;`.

- [ ] **Step 4: Run full test suite to verify all tests pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~LoadBalancingMultiReplicaTests|FullyQualifiedName~ArchitectureTests|FullyQualifiedName~AntiDdosCacheAndOutputCacheTests|FullyQualifiedName~OrleansFoundationUnitTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/Orleans/OrleansSetup.cs apps/backend/src/Infrastructure/Http/RedisOutputCacheStore.cs apps/backend/src/Infrastructure/Http/HttpPerformanceSetup.cs apps/backend/src/Infrastructure/Http/ReverseProxyOptions.cs apps/backend/src/Infrastructure/Http/ReverseProxySetup.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/LoadBalancingMultiReplicaTests.cs
git commit -m "feat(infra): add Orleans container IP discovery, OutputCache backplane sync, and X-Upstream-Instance header"
```
