namespace ExamPrism.Tests.Infrastructure;

using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Common.MultiTenancy;
using Common.Resilience;
using Common.Telemetry;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Resilience;
using ExamPrism.Infrastructure.Telemetry;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Polly.Registry;
using global::Tests.Infrastructure;
using Wolverine;
using Xunit;

public class ResiliencePersistenceAndTelemetryTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ResiliencePersistenceAndTelemetryTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void TenantConnectionStringNormalizer_AppliesNoResetOnClose_KeepAlive_AndReadReplicaHostOverride()
    {
        var options = Options.Create(new MultiTenancyOptions
        {
            DatabasePool = new TenantDatabasePoolOptions
            {
                NoResetOnClose = true,
                KeepAliveSeconds = 30,
                MaxPoolSize = 25,
                MinPoolSize = 2,
                ConnectionIdleLifetimeSeconds = 45,
                ConnectionPruningIntervalSeconds = 15
            }
        });

        var normalizer = new TenantConnectionStringNormalizer(options);
        const string baseConn = "Host=primary.db.internal;Port=5432;Database=examprism_t1;Username=u;Password=p";

        // 1. Primary normalization applies pool settings
        var normalized = normalizer.Normalize(baseConn);
        var parsed = new NpgsqlConnectionStringBuilder(normalized);
        parsed.NoResetOnClose.Should().BeTrue();
        parsed.KeepAlive.Should().Be(30);
        parsed.MaxPoolSize.Should().Be(25);
        parsed.MinPoolSize.Should().Be(2);
        parsed.Host.Should().Be("primary.db.internal");

        // 2. Read replica override rewrites host and target session attributes
        var replicaConn = normalizer.NormalizeForReadReplica(baseConn, "replica.db.internal");
        var parsedReplica = new NpgsqlConnectionStringBuilder(replicaConn);
        parsedReplica.Host.Should().Be("replica.db.internal");
        parsedReplica["Target Session Attributes"].Should().Be("prefer-standby");
        parsedReplica.NoResetOnClose.Should().BeTrue();
        parsedReplica.KeepAlive.Should().Be(30);

        // 3. Fallback to primary when replicaHostOverride is blank
        var fallbackConn = normalizer.NormalizeForReadReplica(baseConn, null);
        fallbackConn.Should().Be(normalized);

        var emptyFallbackConn = normalizer.NormalizeForReadReplica(baseConn, "   ");
        emptyFallbackConn.Should().Be(normalized);
    }

    [Fact]
    public void SlowQueryTelemetryInterceptor_LogsWarning_AndIncrementsMetricWhenDurationExceedsThreshold()
    {
        ExamPrismDiagnostics.ResetForTesting();

        var persistenceOptions = Options.Create(new PersistenceOptions
        {
            SlowQueryThresholdMs = 500
        });

        var testLogger = new TestInterceptorLogger();
        var interceptor = new SlowQueryTelemetryInterceptor(persistenceOptions, testLogger);

        // Fast query (under threshold) - should not log or increment
        interceptor.RecordIfSlow("SELECT 1 FROM exams", TimeSpan.FromMilliseconds(200));
        ExamPrismDiagnostics.SlowQueriesCount.Should().Be(0);
        testLogger.Logs.Should().BeEmpty();

        // Slow query (exceeds threshold) - logs warning and increments counter
        interceptor.RecordIfSlow("SELECT * FROM exams", TimeSpan.FromMilliseconds(600));
        ExamPrismDiagnostics.SlowQueriesCount.Should().Be(1);
        testLogger.Logs.Should().ContainSingle(l =>
            l.Level == LogLevel.Warning &&
            l.Message.Contains("Slow query detected") &&
            l.Message.Contains("SELECT * FROM exams"));
    }

    [Fact]
    public async Task TenantWolverineMiddleware_BeforeAsync_AwaitsStoreAsynchronously()
    {
        var middleware = new TenantWolverineMiddleware();
        var envelope = new Envelope { TenantId = "tenant-async-test" };
        var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null));

        var setter = new TestMultiTenantContextSetter();
        var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();

        var expectedTenant = new ExamPrismTenantInfo("async-guid", "tenant-async-test")
        {
            Status = TenantStatus.Active
        };

        store.GetByIdentifierAsync("tenant-async-test").Returns(Task.FromResult<ExamPrismTenantInfo?>(expectedTenant));

        await middleware.BeforeAsync(envelope, accessor, setter, store);

        setter.MultiTenantContext.Should().NotBeNull();
        setter.MultiTenantContext!.TenantInfo.Should().Be(expectedTenant);
        await store.Received(1).GetByIdentifierAsync("tenant-async-test");
    }

    [Fact]
    public async Task ResilienceAndTelemetry_RegisterDefaultIoPipeline_AndExposePrometheusMetricsEndpoint()
    {
        // 1. Verify ResiliencePipelineProvider resolves DefaultIo pipeline
        var pipelineProvider = _factory.Services.GetRequiredService<ResiliencePipelineProvider<string>>();
        var pipeline = pipelineProvider.GetPipeline(ResiliencePipelineNames.DefaultIo);
        pipeline.Should().NotBeNull();

        var executionResult = await pipeline.ExecuteAsync(async ct =>
        {
            await Task.Yield();
            return 42;
        });
        executionResult.Should().Be(42);

        // 2. Increment counters to verify metric scraping
        ExamPrismDiagnostics.ResetForTesting();
        ExamPrismDiagnostics.RecordSlowQuery("SELECT 1", 550);
        ExamPrismDiagnostics.RecordIdempotencyReplay();

        // 3. Request /metrics endpoint
        var client = _factory.CreatePlatformClient();
        var response = await client.GetAsync("/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/plain");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("examprism_db_slow_queries_total");
        content.Should().Contain("examprism_idempotency_replays_total");
    }

    private sealed class TestInterceptorLogger : ILogger<SlowQueryTelemetryInterceptor>
    {
        public List<(LogLevel Level, string Message)> Logs { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class TestMultiTenantContextSetter : IMultiTenantContextSetter
    {
        public IMultiTenantContext? MultiTenantContext { get; set; }
    }
}
