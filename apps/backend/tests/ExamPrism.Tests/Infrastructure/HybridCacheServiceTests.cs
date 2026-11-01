namespace Tests.Infrastructure;

using System.Buffers;
using Common.Caching;
using ExamPrism.Infrastructure.Caching;
using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Xunit;

public class HybridCacheServiceTests
{
    public record SampleDto(string Name, int Value);

    [Fact]
    public async Task GetOrCreateAsync_CachesFactoryValue_AndDoesNotReinvokeFactoryWithinTtl()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<CacheOptions>(_ => { });
        services.AddHybridCache();
        services.AddSingleton<ICacheService, HybridCacheService>();

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var factoryCount = 0;
        ValueTask<SampleDto> Factory(CancellationToken ct)
        {
            factoryCount++;
            return ValueTask.FromResult(new SampleDto("Exam1", 100));
        }

        var result1 = await cache.GetOrCreateAsync("exam:1", Factory);
        var result2 = await cache.GetOrCreateAsync("exam:1", Factory);

        result1.Should().NotBeNull();
        result1.Name.Should().Be("Exam1");
        result2.Should().Be(result1);
        factoryCount.Should().Be(1);
    }

    [Fact]
    public async Task GetOrDefaultAsync_OnMiss_ReturnsNullWithoutPopulatingCache_AndOnHit_ReturnsCachedValue()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<CacheOptions>(_ => { });
        services.AddHybridCache();
        services.AddSingleton<ICacheService, HybridCacheService>();

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var miss = await cache.GetOrDefaultAsync<SampleDto>("missing");
        miss.Should().BeNull();

        var created = await cache.GetOrCreateAsync("missing", _ => ValueTask.FromResult(new SampleDto("Found", 42)));
        created.Should().NotBeNull();
        created.Name.Should().Be("Found");

        var hit = await cache.GetOrDefaultAsync<SampleDto>("missing");
        hit.Should().NotBeNull();
        hit.Should().Be(created);
    }

    [Fact]
    public async Task SetAsync_RemoveAsync_And_RemoveByTagAsync_MutateAndInvalidateEntries()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<CacheOptions>(_ => { });
        services.AddHybridCache();
        services.AddSingleton<ICacheService, HybridCacheService>();

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.SetAsync("quiz:1", new SampleDto("Q1", 1), tags: ["exam-list"]);
        await cache.SetAsync("quiz:2", new SampleDto("Q2", 2), tags: ["exam-list"]);
        await cache.SetAsync("quiz:3", new SampleDto("Q3", 3), tags: ["other"]);

        (await cache.GetOrDefaultAsync<SampleDto>("quiz:1")).Should().NotBeNull();
        (await cache.GetOrDefaultAsync<SampleDto>("quiz:2")).Should().NotBeNull();
        (await cache.GetOrDefaultAsync<SampleDto>("quiz:3")).Should().NotBeNull();

        await cache.RemoveByTagAsync("exam-list");

        (await cache.GetOrDefaultAsync<SampleDto>("quiz:1")).Should().BeNull();
        (await cache.GetOrDefaultAsync<SampleDto>("quiz:2")).Should().BeNull();
        (await cache.GetOrDefaultAsync<SampleDto>("quiz:3")).Should().NotBeNull();

        await cache.RemoveAsync("quiz:3");
        (await cache.GetOrDefaultAsync<SampleDto>("quiz:3")).Should().BeNull();
    }

    [Fact]
    public async Task SetAsync_WithShortExpirationOverride_ClampsLocalExpiration_AndExpiresOnFakeTimeProvider()
    {
        var fakeTime = new FakeTimeProvider();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(fakeTime);
        services.Configure<MemoryCacheOptions>(options => options.Clock = new FakeSystemClock(fakeTime));
        services.Configure<CacheOptions>(options =>
        {
            options.DefaultLocalExpirationMinutes = 5;
            options.DefaultExpirationMinutes = 30;
        });
        services.AddHybridCache();
        services.AddSingleton<ICacheService, HybridCacheService>();

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        await cache.SetAsync("short", "val", expiration: TimeSpan.FromSeconds(10));

        (await cache.GetOrDefaultAsync<string>("short")).Should().Be("val");

        fakeTime.Advance(TimeSpan.FromSeconds(11));

        (await cache.GetOrDefaultAsync<string>("short")).Should().BeNull();
    }

    private sealed class FakeSystemClock(TimeProvider timeProvider) : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    }

    [Fact]
    public async Task ResilientDistributedCache_WhenInnerThrowsRedisExceptionOrTimeout_FailsOpenOnBothStandardAndBufferPaths()
    {
        var inner = Substitute.For<IDistributedCache, IBufferDistributedCache>();
        var bufferMock = (IBufferDistributedCache)inner;

        var redisEx = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
        var timeoutEx = new RedisTimeoutException("timeout", CommandStatus.Unknown);

        inner.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(redisEx);
        inner.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>()).ThrowsAsync(timeoutEx);
        inner.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(redisEx);

        bufferMock.TryGetAsync(Arg.Any<string>(), Arg.Any<IBufferWriter<byte>>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromException<bool>(redisEx));
        bufferMock.SetAsync(Arg.Any<string>(), Arg.Any<ReadOnlySequence<byte>>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromException(timeoutEx));

        var logger = NullLogger<ResilientDistributedCache>.Instance;
        var resilientCache = new ResilientDistributedCache(inner, logger);

        // Standard IDistributedCache methods fail open
        var bytes = await resilientCache.GetAsync("k1");
        bytes.Should().BeNull();

        Func<Task> actSet = async () => await resilientCache.SetAsync("k1", [1, 2, 3], new DistributedCacheEntryOptions());
        await actSet.Should().NotThrowAsync();

        Func<Task> actRemove = async () => await resilientCache.RemoveAsync("k1");
        await actRemove.Should().NotThrowAsync();

        // IBufferDistributedCache methods fail open
        var writer = new ArrayBufferWriter<byte>();
        var got = await resilientCache.TryGetAsync("k1", writer);
        got.Should().BeFalse();

        Func<Task> actBufferSet = async () => await resilientCache.SetAsync("k1", new ReadOnlySequence<byte>([1, 2, 3]), new DistributedCacheEntryOptions());
        await actBufferSet.Should().NotThrowAsync();

        // Wire into HybridCache and verify GetOrCreateAsync succeeds despite L2 failures
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<CacheOptions>(_ => { });
        services.AddSingleton<IDistributedCache>(resilientCache);
        services.AddHybridCache();
        services.AddSingleton<ICacheService, HybridCacheService>();

        var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<ICacheService>();

        var result = await cache.GetOrCreateAsync("resilient-key", _ => ValueTask.FromResult("served-via-factory"));
        result.Should().Be("served-via-factory");
    }

    [Fact]
    public async Task ResilientDistributedCache_WhenInnerThrowsObjectDisposedException_FailsOpen()
    {
        var inner = Substitute.For<IDistributedCache>();
        var disposedEx = new ObjectDisposedException("RedisCache");

        inner.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(disposedEx);
        inner.SetAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<DistributedCacheEntryOptions>(), Arg.Any<CancellationToken>()).ThrowsAsync(disposedEx);
        inner.RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(disposedEx);

        var logger = NullLogger<ResilientDistributedCache>.Instance;
        var resilientCache = new ResilientDistributedCache(inner, logger);

        var bytes = await resilientCache.GetAsync("k1");
        bytes.Should().BeNull();

        Func<Task> actSet = async () => await resilientCache.SetAsync("k1", [1, 2, 3], new DistributedCacheEntryOptions());
        await actSet.Should().NotThrowAsync();

        Func<Task> actRemove = async () => await resilientCache.RemoveAsync("k1");
        await actRemove.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RedisHealthCheck_ReturnsHealthyOnPing_AndUnhealthyWhenPingThrows()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.PingAsync(Arg.Any<CommandFlags>()).Returns(Task.FromResult(TimeSpan.FromMilliseconds(1)));

        var check = new RedisHealthCheck(multiplexer);
        var context = new HealthCheckContext();

        var healthyResult = await check.CheckHealthAsync(context);
        healthyResult.Status.Should().Be(HealthStatus.Healthy);

        db.PingAsync(Arg.Any<CommandFlags>()).ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "failed"));

        var unhealthyResult = await check.CheckHealthAsync(context);
        unhealthyResult.Status.Should().Be(HealthStatus.Unhealthy);
        unhealthyResult.Description.Should().Contain("Redis connection failed");
    }
}
