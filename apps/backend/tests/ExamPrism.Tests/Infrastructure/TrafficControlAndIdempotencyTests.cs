namespace ExamPrism.Tests.Infrastructure;

using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Common.Caching;
using Common.Http;
using Common.Idempotency;
using Common.Locking;
using Common.RateLimiting;
using Common.Telemetry;
using ExamPrism.Infrastructure.Http;
using ExamPrism.Infrastructure.Idempotency;
using ExamPrism.Infrastructure.RateLimiting;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;
using Xunit;

public class TrafficControlAndIdempotencyTests
{
    [Fact]
    public async Task InMemorySlidingWindowRateLimiter_And_RedisSlidingWindowRateLimiter_EnforceSlidingWindowAndFailOpen()
    {
        // 1. InMemorySlidingWindowRateLimiter
        var baseTime = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var fakeTime = new FakeTimeProvider(baseTime);
        var window = TimeSpan.FromSeconds(60);
        const int segments = 6; // 10s per segment
        const int limit = 2;

        using var inMemoryLimiter = new InMemorySlidingWindowRateLimiter(
            permitLimit: limit,
            window: window,
            segmentsPerWindow: segments,
            timeProvider: fakeTime);

        // Acquire 1 permit in segment 0 (t = 0s)
        using (var lease1 = await inMemoryLimiter.AcquireAsync(1))
        {
            lease1.IsAcquired.Should().BeTrue();
        }

        // Advance 20 seconds -> segment 2 (t = 20s)
        fakeTime.Advance(TimeSpan.FromSeconds(20));

        // Acquire 1 permit in segment 2
        using (var lease2 = await inMemoryLimiter.AcquireAsync(1))
        {
            lease2.IsAcquired.Should().BeTrue();
        }

        // 3rd attempt at t = 20s should be blocked (limit is 2)
        using (var lease3 = await inMemoryLimiter.AcquireAsync(1))
        {
            lease3.IsAcquired.Should().BeFalse();
            lease3.TryGetMetadata(MetadataName.RetryAfter, out var retry).Should().BeTrue();
            retry.Should().BeGreaterThan(TimeSpan.Zero);
        }

        // Advance to t = 60s (advance 40s): segment 0 (at t=0s) has now expired from the 60s window!
        fakeTime.Advance(TimeSpan.FromSeconds(40));

        // Now segment 0's permit is smoothly recovered; acquire succeeds
        using (var lease4 = await inMemoryLimiter.AcquireAsync(1))
        {
            lease4.IsAcquired.Should().BeTrue();
        }

        // But limit is reached again (segment 2's permit + lease4's permit = 2)
        using (var lease5 = await inMemoryLimiter.AcquireAsync(1))
        {
            lease5.IsAcquired.Should().BeFalse();
        }

        // Advance 21 seconds to t = 81s: segment 2 (at t=20s) has now also expired!
        fakeTime.Advance(TimeSpan.FromSeconds(21));

        using (var lease6 = await inMemoryLimiter.AcquireAsync(1))
        {
            lease6.IsAcquired.Should().BeTrue();
        }

        // 2. RedisSlidingWindowRateLimiter
        var redisMultiplexer = Substitute.For<IConnectionMultiplexer>();
        var redisDb = Substitute.For<IDatabase>();
        redisMultiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);

        // First call: { 1, 1, 0 } (allowed, 1 remaining, 0 retryMs)
        // Second call: { 1, 0, 0 } (allowed, 0 remaining, 0 retryMs)
        // Third call: { 0, 0, 15000 } (rejected, 0 remaining, 15000ms retry)
        redisDb.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(RedisResult.Create(new RedisResult[] { RedisResult.Create(1L), RedisResult.Create(1L), RedisResult.Create(0L) })),
                Task.FromResult(RedisResult.Create(new RedisResult[] { RedisResult.Create(1L), RedisResult.Create(0L), RedisResult.Create(0L) })),
                Task.FromResult(RedisResult.Create(new RedisResult[] { RedisResult.Create(0L), RedisResult.Create(0L), RedisResult.Create(15000L) })));

        using var redisLimiter = new RedisSlidingWindowRateLimiter(
            redisMultiplexer,
            redisKey: "examprism:ratelimit:exam:user1",
            permitLimit: 2,
            window: TimeSpan.FromSeconds(60),
            timeProvider: fakeTime,
            logger: NullLogger.Instance);

        using (var rLease1 = await redisLimiter.AcquireAsync(1))
        {
            rLease1.IsAcquired.Should().BeTrue();
        }

        using (var rLease2 = await redisLimiter.AcquireAsync(1))
        {
            rLease2.IsAcquired.Should().BeTrue();
        }

        using (var rLease3 = await redisLimiter.AcquireAsync(1))
        {
            rLease3.IsAcquired.Should().BeFalse();
            rLease3.TryGetMetadata(MetadataName.RetryAfter, out var retry).Should().BeTrue();
            retry.Should().Be(TimeSpan.FromMilliseconds(15000));
        }

        // Fail open when Redis throws RedisTimeoutException
        redisDb.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .ThrowsAsync(new RedisTimeoutException("Redis command timed out", CommandStatus.Unknown));

        using (var failOpenLease = await redisLimiter.AcquireAsync(1))
        {
            failOpenLease.IsAcquired.Should().BeTrue();
        }
    }

    [Fact]
    public async Task IdempotencyEndpointFilter_ReplaysMatchingPayload_RejectsMutatedPayloadWith422_AndRejectsConcurrentInFlightWith409()
    {
        ExamPrismDiagnostics.ResetForTesting();

        var cache = Substitute.For<ICacheService>();
        var distributedLock = Substitute.For<IDistributedLock>();
        var lockHandle = Substitute.For<IDistributedLockHandle>();
        lockHandle.Resource.Returns("test-resource");
        lockHandle.LockToken.Returns("token-1");

        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IDistributedLockHandle?>(lockHandle));

        var cacheStore = new Dictionary<string, IdempotencyCacheEntry>();
        cache.SetAsync(
            Arg.Any<string>(),
            Arg.Any<IdempotencyCacheEntry>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var k = callInfo.ArgAt<string>(0);
                var entry = callInfo.ArgAt<IdempotencyCacheEntry>(1);
                cacheStore[k] = entry;
                return ValueTask.CompletedTask;
            });

        cache.GetOrDefaultAsync<IdempotencyCacheEntry>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var k = callInfo.ArgAt<string>(0);
                cacheStore.TryGetValue(k, out var entry);
                return ValueTask.FromResult<IdempotencyCacheEntry?>(entry);
            });

        var options = new IdempotencyOptions
        {
            HeaderName = "Idempotency-Key",
            ExpirationMinutes = 60,
            LockTimeoutSeconds = 15
        };

        var filter = new IdempotencyEndpointFilter(
            cache,
            distributedLock,
            options,
            NullLogger<IdempotencyEndpointFilter>.Instance);

        const string jsonBody = """{"examId":"exam-42","submissionId":"sub-100"}""";
        var nextCount = 0;
        EndpointFilterDelegate next = _ =>
        {
            nextCount++;
            return ValueTask.FromResult<object?>(Results.Ok(new { status = "accepted" }));
        };

        // --- 1. First call: executes next once and caches the result ---
        var httpContext1 = new DefaultHttpContext();
        httpContext1.Request.Method = "POST";
        httpContext1.Request.Path = "/api/v1/exams/42/submit";
        httpContext1.Request.Headers["Idempotency-Key"] = "idemp-key-001";
        httpContext1.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));

        var ctx1 = EndpointFilterInvocationContext.Create(httpContext1);
        var result1 = await filter.InvokeAsync(ctx1, next);

        nextCount.Should().Be(1);
        result1.Should().NotBeNull();
        cacheStore.Values.Should().NotBeEmpty();
        var storedEntry = cacheStore.Values.First();
        storedEntry.StatusCode.Should().Be(200);
        storedEntry.PayloadHash.Should().NotBeNullOrWhiteSpace();

        // --- 2. Second call with identical payload: returns cached response without invoking next ---
        var httpContext2 = new DefaultHttpContext();
        httpContext2.Request.Method = "POST";
        httpContext2.Request.Path = "/api/v1/exams/42/submit";
        httpContext2.Request.Headers["Idempotency-Key"] = "idemp-key-001";
        httpContext2.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));

        var ctx2 = EndpointFilterInvocationContext.Create(httpContext2);
        var result2 = await filter.InvokeAsync(ctx2, next);

        nextCount.Should().Be(1, "next must not be invoked on replay");
        httpContext2.Response.Headers["X-Idempotency-Replayed"].ToString().Should().Be("true");
        ExamPrismDiagnostics.IdempotencyReplaysCount.Should().Be(1);
        var replayStatus = (result2 as IStatusCodeHttpResult)?.StatusCode
            ?? httpContext2.Response.StatusCode;
        replayStatus.Should().Be(200);

        // --- 3. Third call with SAME key but DIFFERENT payload: returns 422 Unprocessable Entity ---
        const string mutatedJsonBody = """{"examId":"exam-42","submissionId":"sub-mutated"}""";
        var httpContext3 = new DefaultHttpContext();
        httpContext3.Request.Method = "POST";
        httpContext3.Request.Path = "/api/v1/exams/42/submit";
        httpContext3.Request.Headers["Idempotency-Key"] = "idemp-key-001";
        httpContext3.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(mutatedJsonBody));

        var ctx3 = EndpointFilterInvocationContext.Create(httpContext3);
        var result3 = await filter.InvokeAsync(ctx3, next);

        nextCount.Should().Be(1, "next must not be invoked on payload mismatch");
        var problem3 = result3.Should().BeOfType<ProblemHttpResult>().Subject;
        problem3.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        problem3.ProblemDetails.Status.Should().Be(StatusCodes.Status422UnprocessableEntity);
        problem3.ProblemDetails.Detail.Should().Contain("different request payload");

        // --- 4. Concurrent call: IDistributedLock returns null -> returns 409 Conflict with Retry-After: 1 ---
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IDistributedLockHandle?>(null));

        var httpContext4 = new DefaultHttpContext();
        httpContext4.Request.Method = "POST";
        httpContext4.Request.Path = "/api/v1/exams/42/submit";
        httpContext4.Request.Headers["Idempotency-Key"] = "idemp-key-inflight";
        httpContext4.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonBody));

        var ctx4 = EndpointFilterInvocationContext.Create(httpContext4);
        var result4 = await filter.InvokeAsync(ctx4, next);

        nextCount.Should().Be(1, "next must not be invoked on lock conflict");
        var problem4 = result4.Should().BeOfType<ProblemHttpResult>().Subject;
        problem4.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        httpContext4.Response.Headers.RetryAfter.ToString().Should().Be("1");
        ExamPrismDiagnostics.IdempotencyConflictsCount.Should().Be(1);
    }

    [Fact]
    public async Task IdempotencyEndpointFilter_RejectsKeyExceeding128Characters_With400BadRequest()
    {
        var cache = Substitute.For<ICacheService>();
        var distributedLock = Substitute.For<IDistributedLock>();
        var filter = new IdempotencyEndpointFilter(cache, distributedLock);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = "POST";
        httpContext.Request.Path = "/api/v1/exams/submit";
        httpContext.Request.Headers["Idempotency-Key"] = new string('x', 129);
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));

        var ctx = EndpointFilterInvocationContext.Create(httpContext);
        var nextInvoked = false;
        var result = await filter.InvokeAsync(ctx, _ => { nextInvoked = true; return ValueTask.FromResult<object?>(Results.Ok()); });

        nextInvoked.Should().BeFalse();
        var problem = result.Should().BeOfType<ProblemHttpResult>().Subject;
        problem.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public void RateLimitPolicies_And_RequestTimeoutPolicies_DeclareExpectedConstants()
    {
        RateLimitPolicies.Auth.Should().Be("auth");
        RateLimitPolicies.ExamCritical.Should().Be("ExamCritical");
        RateLimitPolicies.HeavyOperationBulkhead.Should().Be("HeavyOperationBulkhead");

        RequestTimeoutPolicies.Default.Should().Be("Default");
        RequestTimeoutPolicies.Fast.Should().Be("Fast");
        RequestTimeoutPolicies.ExamSubmit.Should().Be("ExamSubmit");
        RequestTimeoutPolicies.LongRunning.Should().Be("LongRunning");
    }
}
