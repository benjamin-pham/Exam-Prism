namespace ExamPrism.Tests.Infrastructure;

using System.Xml.Linq;
using Common.Caching;
using Common.Locking;
using Common.Telemetry;
using ExamPrism.Infrastructure.Caching;
using ExamPrism.Infrastructure.Locking;
using ExamPrism.Infrastructure.MultiTenancy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Xunit;

public class ScaleOutPrimitivesTests
{
    [Fact]
    public async Task RedisCacheBackplane_PublishesKeyAndTagInvalidations_EvictsRemoteL1_AndIgnoresSelfMessages()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var subscriber = Substitute.For<ISubscriber>();
        multiplexer.GetSubscriber(Arg.Any<object>()).Returns(subscriber);

        Action<RedisChannel, RedisValue>? capturedHandler = null;
        subscriber.SubscribeAsync(
            Arg.Any<RedisChannel>(),
            Arg.Do<Action<RedisChannel, RedisValue>>(h => capturedHandler = h),
            Arg.Any<CommandFlags>())
            .Returns(Task.CompletedTask);

        var hybridCache = Substitute.For<HybridCache>();
        var options = Options.Create(new CacheOptions
        {
            KeyPrefix = "examprism:",
            BackplaneChannelName = "cache:backplane"
        });
        var logger = NullLogger<RedisCacheBackplane>.Instance;
        var backplane = new RedisCacheBackplane(multiplexer, hybridCache, options, logger);

        await backplane.StartAsync(CancellationToken.None);

        capturedHandler.Should().NotBeNull();

        // 1. Verify publishing key invalidation
        await backplane.PublishKeyInvalidationAsync("t:1:k1");
        await subscriber.Received(1).PublishAsync(
            Arg.Is<RedisChannel>(c => c == "examprism:cache:backplane"),
            Arg.Is<RedisValue>(v => (string?)v == $"{backplane.InstanceId}|K|t:1:k1"),
            Arg.Any<CommandFlags>());

        // 2. Verify publishing tag invalidation
        await backplane.PublishTagInvalidationAsync("t:1:tag1");
        await subscriber.Received(1).PublishAsync(
            Arg.Is<RedisChannel>(c => c == "examprism:cache:backplane"),
            Arg.Is<RedisValue>(v => (string?)v == $"{backplane.InstanceId}|T|t:1:tag1"),
            Arg.Any<CommandFlags>());

        // 3. Remote message -> L1 evicted + ExamPrismDiagnostics counter incremented
        var initialEvictions = ExamPrismDiagnostics.CacheBackplaneEvictionsCount;
        capturedHandler!(RedisChannel.Literal("examprism:cache:backplane"), "otherInstance|K|t:1:k1");
        await hybridCache.Received(1).RemoveAsync("t:1:k1", Arg.Any<CancellationToken>());
        ExamPrismDiagnostics.CacheBackplaneEvictionsCount.Should().Be(initialEvictions + 1);

        // 4. Remote tag message -> L1 evicted by tag + counter incremented
        capturedHandler(RedisChannel.Literal("examprism:cache:backplane"), "otherInstance|T|t:1:tag1");
        await hybridCache.Received(1).RemoveByTagAsync("t:1:tag1", Arg.Any<CancellationToken>());
        ExamPrismDiagnostics.CacheBackplaneEvictionsCount.Should().Be(initialEvictions + 2);

        // 5. Self message -> ignored (no RemoveAsync call, counter not incremented)
        capturedHandler(RedisChannel.Literal("examprism:cache:backplane"), $"{backplane.InstanceId}|K|t:1:k2");
        await hybridCache.DidNotReceive().RemoveAsync("t:1:k2", Arg.Any<CancellationToken>());
        ExamPrismDiagnostics.CacheBackplaneEvictionsCount.Should().Be(initialEvictions + 2);
    }

    [Fact]
    public async Task InMemoryDistributedLock_And_RedisDistributedLock_EnforceMutualExclusion_AndVerifyOwnerTokenOnRelease()
    {
        // Part 1: InMemoryDistributedLock
        var inMemoryLock = new InMemoryDistributedLock();
        var handle1 = await inMemoryLock.TryAcquireAsync("res-test-1");
        handle1.Should().NotBeNull();
        handle1!.Resource.Should().Be("res-test-1");
        handle1.LockToken.Should().NotBeNullOrWhiteSpace();

        var handle2 = await inMemoryLock.TryAcquireAsync("res-test-1");
        handle2.Should().BeNull();

        await handle1.DisposeAsync();

        var handle3 = await inMemoryLock.TryAcquireAsync("res-test-1");
        handle3.Should().NotBeNull();
        await handle3!.DisposeAsync();

        // Part 2: RedisDistributedLock
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        database.StringSetAsync(
            Arg.Is<RedisKey>(k => k == "lock:res-redis-1"),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Is<When>(w => w == When.NotExists))
            .Returns(Task.FromResult(true));

        var lockOptions = Options.Create(new LockingOptions { KeyPrefix = "lock:", DefaultExpirySeconds = 30 });
        var redisLock = new RedisDistributedLock(multiplexer, lockOptions, NullLogger<RedisDistributedLock>.Instance);

        var redisHandle = await redisLock.TryAcquireAsync("res-redis-1");
        redisHandle.Should().NotBeNull();
        redisHandle!.Resource.Should().Be("res-redis-1");
        redisHandle.LockToken.Should().NotBeNullOrWhiteSpace();

        await redisHandle.DisposeAsync();

        await database.Received(1).ScriptEvaluateAsync(
            Arg.Is<string>(s => s.Contains("GET") && s.Contains("DEL")),
            Arg.Is<RedisKey[]>(k => k.Length == 1 && k[0] == "lock:res-redis-1"),
            Arg.Is<RedisValue[]>(v => v.Length == 1 && v[0] == redisHandle.LockToken),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public void RedisXmlRepository_StoresAndReadsDataProtectionXmlElements_AndFailsOpenOnRedisException()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var key = "examprism:dataprotection:keys";
        var repo = new RedisXmlRepository(multiplexer, key, NullLogger<RedisXmlRepository>.Instance);

        var element = new XElement("key", new XAttribute("id", "1"), new XElement("creationDate", "2026-11-04"));
        repo.StoreElement(element, "key-1");

        database.Received(1).ListRightPush(
            Arg.Is<RedisKey>(k => k == key),
            Arg.Is<RedisValue>(v => v.ToString().Contains("id=\"1\"")),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());

        database.ListRange(Arg.Is<RedisKey>(k => k == key), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns([new RedisValue(element.ToString(SaveOptions.DisableFormatting))]);

        var elements = repo.GetAllElements();
        elements.Should().ContainSingle();
        elements.First().Attribute("id")?.Value.Should().Be("1");

        // Fail-open check on RedisConnectionException
        var failOpenKey = "examprism:dataprotection:keys:empty-failopen";
        var failOpenRepo = new RedisXmlRepository(multiplexer, failOpenKey, NullLogger<RedisXmlRepository>.Instance);
        database.ListRange(Arg.Is<RedisKey>(k => k == failOpenKey), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Throws(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis unreachable"));

        var failOpenElements = failOpenRepo.GetAllElements();
        failOpenElements.Should().BeEmpty();
    }
}
