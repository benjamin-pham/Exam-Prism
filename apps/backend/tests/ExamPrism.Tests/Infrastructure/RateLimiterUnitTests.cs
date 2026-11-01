namespace Tests.Infrastructure;

using System.Threading.RateLimiting;
using ExamPrism.Infrastructure.RateLimiting;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Xunit;

public class RateLimiterUnitTests
{
    [Fact]
    public async Task InMemoryFixedWindowRateLimiter_AcquiresUpToPermitLimit_RejectsWithPositiveRetryAfter_AndResetsOnWindowAdvance()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 15, TimeSpan.Zero));
        using var limiter = new InMemoryFixedWindowRateLimiter(permitLimit: 2, window: TimeSpan.FromSeconds(60), fakeTime);

        using var lease1 = await limiter.AcquireAsync(1);
        lease1.IsAcquired.Should().BeTrue();

        using var lease2 = await limiter.AcquireAsync(1);
        lease2.IsAcquired.Should().BeTrue();

        using var lease3 = await limiter.AcquireAsync(1);
        lease3.IsAcquired.Should().BeFalse();
        lease3.TryGetMetadata(MetadataName.RetryAfter, out var retry).Should().BeTrue();
        retry.Should().Be(TimeSpan.FromSeconds(45));

        fakeTime.Advance(TimeSpan.FromSeconds(45));

        using var lease4 = await limiter.AcquireAsync(1);
        lease4.IsAcquired.Should().BeTrue();
    }

    [Fact]
    public async Task InMemoryFixedWindowRateLimiter_AtLastTickOfWindow_StillReturnsPositiveRetryAfter_AndValidatesConstructorArgs()
    {
        var window = TimeSpan.FromSeconds(60);
        // Set time 1 tick before the 60-second window ends
        var baseTime = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var oneTickBeforeEnd = baseTime.AddTicks(window.Ticks - 1);
        var fakeTime = new FakeTimeProvider(oneTickBeforeEnd);

        using var limiter = new InMemoryFixedWindowRateLimiter(permitLimit: 1, window: window, fakeTime);

        using var lease1 = await limiter.AcquireAsync(1);
        lease1.IsAcquired.Should().BeTrue();

        using var lease2 = await limiter.AcquireAsync(1);
        lease2.IsAcquired.Should().BeFalse();
        lease2.TryGetMetadata(MetadataName.RetryAfter, out var retry).Should().BeTrue();
        retry.Should().Be(TimeSpan.FromTicks(1));
        retry.Should().BeGreaterThan(TimeSpan.Zero);

        Action actNegativePermit = () => new InMemoryFixedWindowRateLimiter(permitLimit: 0, window: window, fakeTime);
        actNegativePermit.Should().Throw<ArgumentOutOfRangeException>();

        Action actNegativeWindow = () => new InMemoryFixedWindowRateLimiter(permitLimit: 1, window: TimeSpan.Zero, fakeTime);
        actNegativeWindow.Should().Throw<ArgumentOutOfRangeException>();

        Action actNullTimeProvider = () => new InMemoryFixedWindowRateLimiter(permitLimit: 1, window: window, null!);
        actNullTimeProvider.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task RedisFixedWindowRateLimiter_EvaluatesAtomicLuaScript_EnforcesPermitLimit_AndResetsBucketOnTimeAdvance()
    {
        var baseTime = new DateTimeOffset(2026, 11, 1, 12, 0, 10, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(baseTime);

        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        // Sequence of Lua return values: 1, 2, 3
        db.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(RedisResult.Create(1L)),
                Task.FromResult(RedisResult.Create(2L)),
                Task.FromResult(RedisResult.Create(3L)));

        using var limiter = new RedisFixedWindowRateLimiter(
            multiplexer,
            redisKeyPrefix: "examprism:ratelimit:auth:ip:127.0.0.1",
            permitLimit: 2,
            window: TimeSpan.FromSeconds(60),
            fakeTime,
            NullLogger.Instance);

        using var lease1 = await limiter.AcquireAsync(1);
        lease1.IsAcquired.Should().BeTrue();

        using var lease2 = await limiter.AcquireAsync(1);
        lease2.IsAcquired.Should().BeTrue();

        using var lease3 = await limiter.AcquireAsync(1);
        lease3.IsAcquired.Should().BeFalse();
        lease3.TryGetMetadata(MetadataName.RetryAfter, out var retry).Should().BeTrue();
        retry.Should().Be(TimeSpan.FromSeconds(50));

        // Verify the keys and arguments evaluated on the script
        await db.Received(3).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0].ToString().StartsWith("examprism:ratelimit:auth:ip:127.0.0.1:")),
            Arg.Is<RedisValue[]>(args => args.Length == 2 && (long)args[0] == 1 && (long)args[1] == 60000),
            Arg.Any<CommandFlags>());

        // Advance 60 seconds -> bucket changes
        fakeTime.Advance(TimeSpan.FromSeconds(60));
        var expectedNewBucket = fakeTime.GetUtcNow().UtcTicks / TimeSpan.FromSeconds(60).Ticks;

        db.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create(1L)));

        using var lease4 = await limiter.AcquireAsync(1);
        lease4.IsAcquired.Should().BeTrue();

        await db.Received().ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0].ToString() == $"examprism:ratelimit:auth:ip:127.0.0.1:{expectedNewBucket}"),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>());
    }

    [Fact]
    public async Task RedisFixedWindowRateLimiter_WhenRedisThrowsConnectionOrTimeoutException_FailsOpenAndAcquiresLease()
    {
        var fakeTime = new FakeTimeProvider();
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.SocketFailure, "lost"));

        using var limiter = new RedisFixedWindowRateLimiter(
            multiplexer,
            redisKeyPrefix: "examprism:ratelimit:auth:ip:127.0.0.1",
            permitLimit: 2,
            window: TimeSpan.FromSeconds(60),
            fakeTime,
            NullLogger.Instance);

        using var lease1 = await limiter.AcquireAsync(1);
        lease1.IsAcquired.Should().BeTrue();

        db.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisTimeoutException("timeout", CommandStatus.Unknown));

        using var lease2 = await limiter.AcquireAsync(1);
        lease2.IsAcquired.Should().BeTrue();
    }

    [Fact]
    public async Task InMemoryFixedWindowRateLimiter_IdleDuration_TracksLastAcquiredTime()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        using var limiter = new InMemoryFixedWindowRateLimiter(permitLimit: 2, window: TimeSpan.FromSeconds(60), fakeTime);

        limiter.IdleDuration.Should().Be(TimeSpan.Zero);

        using var lease = await limiter.AcquireAsync(1);
        lease.IsAcquired.Should().BeTrue();

        fakeTime.Advance(TimeSpan.FromSeconds(25));
        limiter.IdleDuration.Should().Be(TimeSpan.FromSeconds(25));
    }

    [Fact]
    public async Task RedisFixedWindowRateLimiter_WhenRedisThrowsObjectDisposedException_FailsOpenAndTracksIdleDuration()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var db = Substitute.For<IDatabase>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .ThrowsAsync(new ObjectDisposedException("ConnectionMultiplexer"));

        using var limiter = new RedisFixedWindowRateLimiter(
            multiplexer,
            redisKeyPrefix: "examprism:ratelimit:auth:ip:127.0.0.1",
            permitLimit: 2,
            window: TimeSpan.FromSeconds(60),
            fakeTime,
            NullLogger.Instance);

        limiter.IdleDuration.Should().Be(TimeSpan.Zero);

        using var lease = await limiter.AcquireAsync(1);
        lease.IsAcquired.Should().BeTrue();

        fakeTime.Advance(TimeSpan.FromSeconds(10));
        limiter.IdleDuration.Should().Be(TimeSpan.FromSeconds(10));
    }
}

