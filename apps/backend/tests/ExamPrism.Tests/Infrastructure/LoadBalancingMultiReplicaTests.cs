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

