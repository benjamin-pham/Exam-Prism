namespace ExamPrism.Tests.Infrastructure;

using System.IO;
using System.Text.Json;
using System.Threading;
using Common.Http;
using Common.Telemetry;
using ExamPrism.Infrastructure.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

[Collection("DiagnosticsSerial")]
public class AdaptiveLoadSheddingAndRuntimeTests
{
    [Fact]
    public void AdaptiveConcurrencyLimiter_ReducesLimitUnderLatencySpike_AndShedsByPriorityTier()
    {
        // 1. Initializes AdaptiveConcurrencyLimiter with options
        var options = new LoadSheddingOptions
        {
            InitialConcurrencyLimit = 10,
            MinConcurrencyLimit = 4,
            MaxConcurrencyLimit = 20,
            LowPriorityShedFraction = 0.7,
            NormalPriorityShedFraction = 0.9,
            HighPriorityShedFraction = 1.0,
            CriticalReserveMultiplier = 1.2,
            BaselineLatencyFloorMs = 5.0,
            LatencyGradientThreshold = 0.8,
            SmoothingFactor = 0.2
        };

        var limiter = new AdaptiveConcurrencyLimiter(options);
        limiter.CurrentLimit.Should().Be(10.0);
        limiter.InFlight.Should().Be(0);

        var leases = new List<IDisposable>();

        // 2. Acquires 7 leases (InFlight == 7)
        for (int i = 0; i < 7; i++)
        {
            limiter.TryAcquire(RequestPriority.Low, out var lease).Should().BeTrue();
            lease.Should().NotBeNull();
            leases.Add(lease!);
        }

        limiter.InFlight.Should().Be(7);

        // Low threshold is 10 * 0.7 = 7. InFlight is 7 -> Low rejected
        limiter.TryAcquire(RequestPriority.Low, out var rejectedLow).Should().BeFalse();
        rejectedLow.Should().BeNull();

        // Normal threshold is 10 * 0.9 = 9. InFlight is 7 -> Normal admitted (InFlight becomes 8)
        limiter.TryAcquire(RequestPriority.Normal, out var normalLease).Should().BeTrue();
        normalLease.Should().NotBeNull();
        leases.Add(normalLease!);
        limiter.InFlight.Should().Be(8);

        // 3. Acquires up to 10 leases (InFlight == 10)
        // Normal admitted at 8 -> 9
        limiter.TryAcquire(RequestPriority.Normal, out var normalLease2).Should().BeTrue();
        normalLease2.Should().NotBeNull();
        leases.Add(normalLease2!);
        limiter.InFlight.Should().Be(9);

        // At 9, Normal is shed (9 >= 9), but High is admitted (9 < 10) -> InFlight becomes 10
        limiter.TryAcquire(RequestPriority.High, out var highLease).Should().BeTrue();
        highLease.Should().NotBeNull();
        leases.Add(highLease!);
        limiter.InFlight.Should().Be(10);

        // Normal and High return false at InFlight == 10
        limiter.TryAcquire(RequestPriority.Normal, out var rejectedNormal).Should().BeFalse();
        rejectedNormal.Should().BeNull();
        limiter.TryAcquire(RequestPriority.High, out var rejectedHigh).Should().BeFalse();
        rejectedHigh.Should().BeNull();

        // Critical threshold is 10 * 1.2 = 12. Admitted up to 12
        limiter.TryAcquire(RequestPriority.Critical, out var crit1).Should().BeTrue();
        crit1.Should().NotBeNull();
        leases.Add(crit1!);
        limiter.InFlight.Should().Be(11);

        limiter.TryAcquire(RequestPriority.Critical, out var crit2).Should().BeTrue();
        crit2.Should().NotBeNull();
        leases.Add(crit2!);
        limiter.InFlight.Should().Be(12);

        // At 12, Critical is now also rejected
        limiter.TryAcquire(RequestPriority.Critical, out var crit3).Should().BeFalse();
        crit3.Should().BeNull();
        limiter.InFlight.Should().Be(12);

        // 4. Records high latency samples (RecordSample(500.0) against baseline 5.0ms)
        // verifies CurrentLimit decreases multiplicatively toward MinConcurrencyLimit
        double limitBeforeSpike = limiter.CurrentLimit;
        limitBeforeSpike.Should().Be(10.0);

        limiter.RecordSample(500.0);
        limiter.CurrentLimit.Should().BeLessThan(limitBeforeSpike);

        limiter.RecordSample(500.0);
        limiter.RecordSample(500.0);
        limiter.RecordSample(500.0);
        limiter.CurrentLimit.Should().Be(options.MinConcurrencyLimit);

        // Cleanup leases
        foreach (var lease in leases)
        {
            lease.Dispose();
        }
        limiter.InFlight.Should().Be(0);
    }

    [Fact]
    public async Task AdaptiveLoadSheddingMiddleware_Returns503WithRetryAfterAndHeaderWhenShed_AndExemptsHealthAndMetrics()
    {
        var initialRejected = ExamPrismDiagnostics.LoadSheddingRejectedCount;

        var options = new LoadSheddingOptions
        {
            InitialConcurrencyLimit = 1,
            MinConcurrencyLimit = 1,
            MaxConcurrencyLimit = 10,
            NormalPriorityShedFraction = 1.0,
            RetryAfterSeconds = 2
        };

        var limiter = new AdaptiveConcurrencyLimiter(options);

        // Acquire the 1 available permit so limiter is full
        limiter.TryAcquire(RequestPriority.Normal, out var heldLease).Should().BeTrue();
        heldLease.Should().NotBeNull();

        var middleware = new AdaptiveLoadSheddingMiddleware(
            next: ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            limiter: limiter,
            options: Options.Create(options));

        // 1. Invokes middleware when limiter rejects RequestPriority.Normal request
        var shedContext = new DefaultHttpContext();
        shedContext.Response.Body = new MemoryStream();
        shedContext.Request.Path = "/api/v1/exams/take";

        await middleware.InvokeAsync(shedContext);

        shedContext.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        shedContext.Response.Headers["Retry-After"].ToString().Should().Be("2");
        shedContext.Response.Headers["X-Load-Shed"].ToString().Should().Be("true");
        shedContext.Response.ContentType.Should().Contain("application/problem+json");
        ExamPrismDiagnostics.LoadSheddingRejectedCount.Should().Be(initialRejected + 1);

        shedContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var problem = await JsonSerializer.DeserializeAsync<ProblemDetails>(shedContext.Response.Body);
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(StatusCodes.Status503ServiceUnavailable);

        // 2. Invokes middleware for /health/live and /metrics even when limiter is full
        var healthContext = new DefaultHttpContext();
        healthContext.Response.Body = new MemoryStream();
        healthContext.Request.Path = "/health/live";

        await middleware.InvokeAsync(healthContext);
        healthContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        var metricsContext = new DefaultHttpContext();
        metricsContext.Response.Body = new MemoryStream();
        metricsContext.Request.Path = "/metrics";

        await middleware.InvokeAsync(metricsContext);
        metricsContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        // Verify rejected count remained initial + 1 (health and metrics bypassed)
        ExamPrismDiagnostics.LoadSheddingRejectedCount.Should().Be(initialRejected + 1);

        heldLease!.Dispose();
    }

    [Fact]
    public void RuntimeTuningSetup_AppliesThreadPoolMinimums()
    {
        var options = new RuntimeTuningOptions
        {
            MinWorkerThreads = 64,
            MinCompletionPortThreads = 64
        };

        var success = RuntimeTuningSetup.ApplyThreadPoolTuning(options);
        success.Should().BeTrue();

        ThreadPool.GetMinThreads(out var workerThreads, out var completionPortThreads);
        workerThreads.Should().BeGreaterThanOrEqualTo(64);
        completionPortThreads.Should().BeGreaterThanOrEqualTo(64);
    }

    [Fact]
    public void PriorityEndpointExtensions_WithRequestPriority_AttachesMetadata()
    {
        var builder = new TestEndpointConventionBuilder();
        builder.WithRequestPriority(RequestPriority.Critical);

        var metadata = builder.Metadata.OfType<RequestPriorityMetadata>().FirstOrDefault();
        metadata.Should().NotBeNull();
        metadata!.Priority.Should().Be(RequestPriority.Critical);
    }

    [Fact]
    public void AdaptiveConcurrencyLimiter_MonotonicFloorDrift_ResetsMinLatencyTowardEwmaAfterSampleWindow()
    {
        var options = new LoadSheddingOptions
        {
            InitialConcurrencyLimit = 50,
            MinConcurrencyLimit = 5,
            MaxConcurrencyLimit = 100,
            BaselineLatencyFloorMs = 2.0,
            MinLatencyResetIntervalSamples = 100,
            LatencyGradientThreshold = 0.8,
            SmoothingFactor = 0.2
        };

        var limiter = new AdaptiveConcurrencyLimiter(options);

        // 1. Cold start sample with very low latency (e.g. 1.0ms)
        limiter.RecordSample(1.0);
        limiter.MinLatencyMs.Should().Be(1.0);

        // 2. Traffic transitions to healthy DB traffic at 20.0ms
        // Record 98 samples of 20ms (total samples = 99, right before reset interval 100)
        for (int i = 0; i < 98; i++)
        {
            limiter.RecordSample(20.0);
        }

        // Before reset, min latency is still locked at cold start measurement
        limiter.MinLatencyMs.Should().Be(1.0);
        limiter.EwmaLatencyMs.Should().BeApproximately(20.0, 0.5);

        // 3. 100th sample triggers periodic reset
        limiter.RecordSample(20.0);

        // MinLatencyMs must drift upward toward the current EWMA latency rather than remaining locked at 1.0ms
        limiter.MinLatencyMs.Should().BeGreaterThanOrEqualTo(19.0);
        limiter.MinLatencyMs.Should().BeLessThanOrEqualTo(20.5);
    }

    [Fact]
    public async Task AdaptiveLoadSheddingMiddleware_ClampsUntrustedClientPriorityHeaders_AndRespectsEndpointMetadata()
    {
        var options = new LoadSheddingOptions
        {
            InitialConcurrencyLimit = 10,
            MinConcurrencyLimit = 2,
            MaxConcurrencyLimit = 20,
            LowPriorityShedFraction = 0.5,    // Limit * 0.5 = 5
            NormalPriorityShedFraction = 1.0, // Limit * 1.0 = 10
            CriticalReserveMultiplier = 1.2   // Limit * 1.2 = 12
        };

        var limiter = new AdaptiveConcurrencyLimiter(options);

        // Hold 10 leases so normal priority is saturated (InFlight == 10)
        var leases = new List<IDisposable>();
        for (int i = 0; i < 10; i++)
        {
            limiter.TryAcquire(RequestPriority.Normal, out var lease).Should().BeTrue();
            leases.Add(lease!);
        }

        var middleware = new AdaptiveLoadSheddingMiddleware(
            next: ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return Task.CompletedTask;
            },
            limiter: limiter,
            options: Options.Create(options));

        try
        {
            // Case 1: Untrusted client sends X-Request-Priority: Critical without endpoint metadata
            // Header is clamped to Normal. InFlight is 10 >= Normal threshold 10 -> shed with 503!
            var untrustedCritContext = new DefaultHttpContext();
            untrustedCritContext.Request.Path = "/api/v1/orders";
            untrustedCritContext.Request.Headers["X-Request-Priority"] = "Critical";
            untrustedCritContext.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(untrustedCritContext);
            untrustedCritContext.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

            // Case 2: Untrusted client sends X-Request-Priority: High without endpoint metadata -> shed with 503
            var untrustedHighContext = new DefaultHttpContext();
            untrustedHighContext.Request.Path = "/api/v1/orders";
            untrustedHighContext.Request.Headers["X-Request-Priority"] = "High";
            untrustedHighContext.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(untrustedHighContext);
            untrustedHighContext.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);

            // Case 3: Endpoint with explicit RequestPriorityMetadata(RequestPriority.Critical)
            // Even though InFlight is 10, Critical allows up to 12 -> admitted with 200 OK!
            var criticalEndpointContext = new DefaultHttpContext();
            criticalEndpointContext.Request.Path = "/api/v1/exams/submit";
            var endpoint = new Endpoint(
                _ => Task.CompletedTask,
                new EndpointMetadataCollection(new RequestPriorityMetadata(RequestPriority.Critical)),
                "CriticalEndpoint");
            criticalEndpointContext.SetEndpoint(endpoint);

            await middleware.InvokeAsync(criticalEndpointContext);
            criticalEndpointContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    private sealed class TestEndpointConventionBuilder : IEndpointConventionBuilder
    {
        public List<object> Metadata { get; } = new();

        public void Add(Action<EndpointBuilder> convention)
        {
            var endpointModel = new RouteEndpointBuilder(
                _ => Task.CompletedTask,
                RoutePatternFactory.Parse("/"),
                0);

            convention(endpointModel);

            foreach (var item in endpointModel.Metadata)
            {
                Metadata.Add(item);
            }
        }
    }
}
