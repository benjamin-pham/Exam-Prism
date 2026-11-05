namespace ExamPrism.Tests.Infrastructure;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Common.Caching;
using ExamPrism.Infrastructure.Caching;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
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
        services.AddLogging();
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

    [Fact]
    public async Task HybridCacheService_And_RedisCacheBackplane_InvalidatesL1Only_PreservingSharedRedisL2Entry()
    {
        // Shared L2 storage representing cluster Redis
        var sharedL2Storage = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
        var sharedDistCache = Substitute.For<Microsoft.Extensions.Caching.Distributed.IDistributedCache>();
        sharedDistCache.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<byte[]?>(sharedL2Storage.TryGetValue(ci.ArgAt<string>(0), out var b) ? b : null));
        sharedDistCache.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sharedL2Storage[ci.ArgAt<string>(0)] = ci.ArgAt<byte[]>(1);
                return Task.CompletedTask;
            });
        sharedDistCache.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                sharedL2Storage.TryRemove(ci.ArgAt<string>(0), out _);
                return Task.CompletedTask;
            });

        // Pub/sub backplane routing between Replica A and Replica B
        Action<RedisChannel, RedisValue>? subscriberB = null;
        var subB = Substitute.For<ISubscriber>();
        subB.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<Action<RedisChannel, RedisValue>>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                subscriberB = ci.ArgAt<Action<RedisChannel, RedisValue>>(1);
                return Task.CompletedTask;
            });
        var subA = Substitute.For<ISubscriber>();
        subA.PublishAsync(Arg.Any<RedisChannel>(), Arg.Any<RedisValue>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                subscriberB?.Invoke(ci.ArgAt<RedisChannel>(0), ci.ArgAt<RedisValue>(1));
                return Task.FromResult(1L);
            });

        var muxA = Substitute.For<IConnectionMultiplexer>();
        muxA.GetSubscriber(Arg.Any<object>()).Returns(subA);
        var muxB = Substitute.For<IConnectionMultiplexer>();
        muxB.GetSubscriber(Arg.Any<object>()).Returns(subB);

        // Replica A setup
        var servicesA = new ServiceCollection();
        servicesA.AddSingleton(sharedDistCache);
        servicesA.AddHybridCache();
        using var spA = servicesA.BuildServiceProvider();
        var hybridCacheA = spA.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();
        var cacheOpts = Options.Create(new CacheOptions { KeyPrefix = "examprism:", EnableBackplane = true });
        var backplaneA = new RedisCacheBackplane(muxA, hybridCacheA, cacheOpts, NullLogger<RedisCacheBackplane>.Instance);
        var cacheServiceA = new HybridCacheService(hybridCacheA, cacheOpts, backplaneA);

        // Replica B setup
        var servicesB = new ServiceCollection();
        servicesB.AddSingleton(sharedDistCache);
        servicesB.AddHybridCache();
        using var spB = servicesB.BuildServiceProvider();
        var hybridCacheB = spB.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>();
        var backplaneB = new RedisCacheBackplane(muxB, hybridCacheB, cacheOpts, NullLogger<RedisCacheBackplane>.Instance);
        var cacheServiceB = new HybridCacheService(hybridCacheB, cacheOpts, backplaneB);

        await backplaneB.StartAsync(CancellationToken.None);

        // 1. Replica B warms up its L1 for "user:42" with initial data
        var factoryCallCount = 0;
        var val1 = await cacheServiceB.GetOrCreateAsync<string>("user:42", _ =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return ValueTask.FromResult("version-1");
        });
        val1.Should().Be("version-1");
        factoryCallCount.Should().Be(1);

        // 2. Replica A updates "user:42" with new data via SetAsync and publishes backplane invalidation
        await cacheServiceA.SetAsync("user:42", "version-2");

        // 3. Verify Redis L2 storage still retains the updated entry (was NOT deleted by peer backplane eviction)
        sharedL2Storage.Should().NotBeEmpty();

        // 4. Replica B reads "user:42". Its L1 was evicted by backplane, so it reads "version-2" from L2 Redis
        // without calling the underlying database factory again!
        var val2 = await cacheServiceB.GetOrCreateAsync<string>("user:42", _ =>
        {
            Interlocked.Increment(ref factoryCallCount);
            return ValueTask.FromResult("stale-factory-should-not-run");
        });
        val2.Should().Be("version-2");
        factoryCallCount.Should().Be(1); // Factory was NOT called again
    }
}

