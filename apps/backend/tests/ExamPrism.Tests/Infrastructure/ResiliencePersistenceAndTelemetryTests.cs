namespace ExamPrism.Tests.Infrastructure;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public async Task SlowQueryTelemetryInterceptor_FailedCommandOverThreshold_IncrementsMetricAndLogsWarning()
    {
        ExamPrismDiagnostics.ResetForTesting();
        var testLogger = new TestInterceptorLogger();
        var interceptor = new SlowQueryTelemetryInterceptor(
            Options.Create(new PersistenceOptions { SlowQueryThresholdMs = 500 }),
            testLogger);

        using var command = new NpgsqlCommand("SELECT pg_sleep(60)");

        // Sync failure hook (e.g. command timeout)
        interceptor.CommandFailed(command, CreateErrorEventData(command, TimeSpan.FromSeconds(30)));
        ExamPrismDiagnostics.SlowQueriesCount.Should().Be(1);

        // Async failure hook
        await interceptor.CommandFailedAsync(command, CreateErrorEventData(command, TimeSpan.FromSeconds(30)));
        ExamPrismDiagnostics.SlowQueriesCount.Should().Be(2);

        testLogger.Logs.Should().HaveCount(2).And.OnlyContain(l =>
            l.Level == LogLevel.Warning && l.Message.Contains("SELECT pg_sleep(60)"));

        // Fast failure stays unrecorded
        interceptor.CommandFailed(command, CreateErrorEventData(command, TimeSpan.FromMilliseconds(5)));
        ExamPrismDiagnostics.SlowQueriesCount.Should().Be(2);
    }

    [Fact]
    public void SlowQueryTelemetryInterceptor_PopulatesTenantIdFromMultiTenantContext_WhenAvailable()
    {
        ExamPrismDiagnostics.ResetForTesting();
        var tenantGuid = Guid.NewGuid();
        var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>(
            new ExamPrismTenantInfo(tenantGuid.ToString("D"), "acme") { TenantGuid = tenantGuid, Status = TenantStatus.Active }));

        string? observedTenant = null;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ExamPrismDiagnostics.MeterName && instrument.Name.Contains("slow"))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "tenant.id")
                {
                    observedTenant = tag.Value as string;
                }
            }
        });
        listener.Start();

        var interceptor = new SlowQueryTelemetryInterceptor(
            Options.Create(new PersistenceOptions { SlowQueryThresholdMs = 100 }),
            new TestInterceptorLogger(),
            accessor);

        interceptor.RecordIfSlow("SELECT 1", TimeSpan.FromMilliseconds(250));

        observedTenant.Should().Be(tenantGuid.ToString("D"));
    }

    [Fact]
    public async Task StandardResilienceHandler_DoesNotRetryUnsafeMethods_ButRetriesGet()
    {
        var handler = new CountingFailingHandler();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppResilience(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddHttpClient("unsafe-test").ConfigurePrimaryHttpMessageHandler(() => handler);
        services.Configure<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>("unsafe-test-standard", o =>
        {
            o.Retry.Delay = TimeSpan.FromMilliseconds(1);
            o.Retry.UseJitter = false;
        });

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("unsafe-test");

        var get = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://example.test/x"));
        get.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        handler.GetCalls.Should().BeGreaterThan(1, "GET is idempotent and must be retried");

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Patch })
        {
            handler.Reset();
            var response = await client.SendAsync(new HttpRequestMessage(method, "http://example.test/x"));
            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            handler.TotalCalls.Should().Be(1, $"{method} is not idempotent and must not be retried");
        }
    }

    [Fact]
    public async Task TenantWolverineMiddleware_BeforeAsync_SetsTenantContextSynchronously_ToPreserveAsyncLocalFlow()
    {
        var middleware = new TenantWolverineMiddleware();
        var envelope = new Envelope { TenantId = "tenant-sync-test" };
        var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null));

        var setter = new TestMultiTenantContextSetter();
        var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();

        var expectedTenant = new ExamPrismTenantInfo("sync-guid", "tenant-sync-test")
        {
            Status = TenantStatus.Active
        };

        store.GetByIdentifierAsync("tenant-sync-test").Returns(Task.FromResult<ExamPrismTenantInfo?>(expectedTenant));

        var task = middleware.BeforeAsync(envelope, accessor, setter, store);
        await task;

        setter.MultiTenantContext.Should().NotBeNull();
        setter.MultiTenantContext!.TenantInfo!.Identifier.Should().Be("tenant-sync-test");
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

    private static CommandErrorEventData CreateErrorEventData(NpgsqlCommand command, TimeSpan duration) =>
        new(
            eventDefinition: null!,
            messageGenerator: static (_, _) => string.Empty,
            connection: null!,
            command: command,
            logCommandText: command.CommandText,
            context: null,
            executeMethod: DbCommandMethod.ExecuteReader,
            commandId: Guid.NewGuid(),
            connectionId: Guid.NewGuid(),
            exception: new TimeoutException("command timeout"),
            async: false,
            logParameterValues: false,
            startTime: DateTimeOffset.UtcNow - duration,
            duration: duration,
            commandSource: CommandSource.LinqQuery);

    private sealed class CountingFailingHandler : HttpMessageHandler
    {
        private int _getCalls;
        private int _totalCalls;

        public int GetCalls => Volatile.Read(ref _getCalls);
        public int TotalCalls => Volatile.Read(ref _totalCalls);

        public void Reset()
        {
            Interlocked.Exchange(ref _getCalls, 0);
            Interlocked.Exchange(ref _totalCalls, 0);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _totalCalls);
            if (request.Method == HttpMethod.Get)
            {
                Interlocked.Increment(ref _getCalls);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
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
