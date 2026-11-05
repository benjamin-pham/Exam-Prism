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
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
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
        Action<string>? onKeyInvalidatedOnB = null;
        backplaneB.KeyInvalidated += Arg.Do<Action<string>>(h => onKeyInvalidatedOnB += h);
        backplaneA.When(b => b.PublishKeyInvalidationAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(ci => onKeyInvalidatedOnB?.Invoke(ci.ArgAt<string>(0)));

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
        await backplaneA.Received(1).PublishKeyInvalidationAsync("ddos:ban:ip:203.0.113.50", Arg.Any<CancellationToken>());
        redisStore.Should().ContainKey("examprism:ddos:ban:ip:203.0.113.50");

        // 3. Replica B immediately sees the ban via backplane invalidation of _checkedIps + shared Redis ban key
        var banOnB = await replicaB.GetBanRemainingAsync("203.0.113.50");
        banOnB.Should().NotBeNull();
        banOnB!.Value.TotalSeconds.Should().BeGreaterThan(200);
    }

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
}

