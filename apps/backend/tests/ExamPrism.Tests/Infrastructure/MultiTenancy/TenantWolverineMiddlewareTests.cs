namespace Tests.Infrastructure.MultiTenancy;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FluentAssertions;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Wolverine;
using Xunit;

public class TenantWolverineMiddlewareTests
{
    private sealed class MemorySink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }

    private static (Logger Logger, MemorySink Sink) CreateLoggerWithSink()
    {
        var sink = new MemorySink();
        var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        return (logger, sink);
    }

    [Fact]
    public async Task BeforeAsync_WhenEnvelopeHasTenantGuid_EnrichesLogContextWithTenantId_AndFinallyDisposesIt()
    {
        var (logger, sink) = CreateLoggerWithSink();
        using (logger)
        {
            var tenantId = Guid.NewGuid();
            var tenant = new ExamPrismTenantInfo(tenantId.ToString("D"), "tenant-guid")
            {
                TenantGuid = tenantId,
                Status = TenantStatus.Active
            };

            var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
            accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null));

            var setter = Substitute.For<IMultiTenantContextSetter>();
            var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();
            store.GetAsync(tenantId.ToString("D")).Returns(tenant);

            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = tenantId.ToString("D") };

            await middleware.BeforeAsync(envelope, accessor, setter, store);

            logger.Information("Processing message");
            sink.Events.Should().HaveCount(1);
            sink.Events[0].Properties.Should().ContainKey("TenantId");
            sink.Events[0].Properties["TenantId"].ToString().Should().Contain(tenantId.ToString());

            middleware.Finally(setter);

            logger.Information("After message handling");
            sink.Events.Should().HaveCount(2);
            sink.Events[1].Properties.Should().NotContainKey("TenantId");
        }
    }

    [Fact]
    public async Task BeforeAsync_WhenEnvelopeHasTenantSlug_EnrichesLogContextWithTenantId_AndFinallyDisposesIt()
    {
        var (logger, sink) = CreateLoggerWithSink();
        using (logger)
        {
            var tenantId = Guid.NewGuid();
            var tenant = new ExamPrismTenantInfo(tenantId.ToString("D"), "tenant-slug")
            {
                TenantGuid = tenantId,
                Status = TenantStatus.Active
            };

            var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
            accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null));

            var setter = Substitute.For<IMultiTenantContextSetter>();
            var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();
            store.GetByIdentifierAsync("tenant-slug").Returns(tenant);

            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = "tenant-slug" };

            await middleware.BeforeAsync(envelope, accessor, setter, store);

            logger.Information("Processing slug message");
            sink.Events.Should().HaveCount(1);
            sink.Events[0].Properties.Should().ContainKey("TenantId");
            sink.Events[0].Properties["TenantId"].ToString().Should().Contain(tenantId.ToString());

            middleware.Finally();

            logger.Information("After slug message");
            sink.Events.Should().HaveCount(2);
            sink.Events[1].Properties.Should().NotContainKey("TenantId");
        }
    }

    [Fact]
    public async Task BeforeAsync_WhenTenantAlreadyActiveInAccessor_EnrichesLogContext_WithoutQueryingStore_AndFinallyDisposesIt()
    {
        var (logger, sink) = CreateLoggerWithSink();
        using (logger)
        {
            var tenantId = Guid.NewGuid();
            var tenant = new ExamPrismTenantInfo(tenantId.ToString("D"), "tenant-active")
            {
                TenantGuid = tenantId,
                Status = TenantStatus.Active
            };

            var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
            accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>(tenant));

            var setter = Substitute.For<IMultiTenantContextSetter>();
            var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();

            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = "tenant-active" };

            await middleware.BeforeAsync(envelope, accessor, setter, store);

            await store.DidNotReceiveWithAnyArgs().GetAsync(Arg.Any<string>());
            await store.DidNotReceiveWithAnyArgs().GetByIdentifierAsync(Arg.Any<string>());

            logger.Information("Processing message with existing tenant");
            sink.Events.Should().HaveCount(1);
            sink.Events[0].Properties.Should().ContainKey("TenantId");
            sink.Events[0].Properties["TenantId"].ToString().Should().Contain(tenantId.ToString());

            middleware.Finally(setter);

            logger.Information("After message with existing tenant");
            sink.Events.Should().HaveCount(2);
            sink.Events[1].Properties.Should().NotContainKey("TenantId");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BeforeAsync_WhenEnvelopeTenantIdIsNullOrEmpty_DoesNotEnrichLogContext(string? tenantId)
    {
        var (logger, sink) = CreateLoggerWithSink();
        using (logger)
        {
            var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
            accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null));

            var setter = Substitute.For<IMultiTenantContextSetter>();
            var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();

            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = tenantId };

            await middleware.BeforeAsync(envelope, accessor, setter, store);

            logger.Information("Message without tenant");
            sink.Events.Should().HaveCount(1);
            sink.Events[0].Properties.Should().NotContainKey("TenantId");

            middleware.Finally(setter);

            logger.Information("After message without tenant");
            sink.Events.Should().HaveCount(2);
            sink.Events[1].Properties.Should().NotContainKey("TenantId");
        }
    }

    [Theory]
    [InlineData("*DEFAULT*")]
    [InlineData("*default*")]
    public async Task BeforeAsync_WhenEnvelopeTenantIdIsDefault_DoesNotEnrichLogContext(string tenantId)
    {
        var (logger, sink) = CreateLoggerWithSink();
        using (logger)
        {
            var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
            accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null));

            var setter = Substitute.For<IMultiTenantContextSetter>();
            var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();

            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = tenantId };

            await middleware.BeforeAsync(envelope, accessor, setter, store);

            logger.Information("Message with default tenant");
            sink.Events.Should().HaveCount(1);
            sink.Events[0].Properties.Should().NotContainKey("TenantId");

            middleware.Finally(setter);

            logger.Information("After message with default tenant");
            sink.Events.Should().HaveCount(2);
            sink.Events[1].Properties.Should().NotContainKey("TenantId");
        }
    }

    [Fact]
    public async Task BeforeAsync_WhenTenantInactive_ThrowsAndDoesNotEnrichLogContext()
    {
        var (logger, sink) = CreateLoggerWithSink();
        using (logger)
        {
            var suspendedTenant = new ExamPrismTenantInfo(Guid.NewGuid().ToString("D"), "tenant-suspended")
            {
                Status = TenantStatus.Suspended
            };

            var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
            accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null));

            var setter = Substitute.For<IMultiTenantContextSetter>();
            var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();
            store.GetByIdentifierAsync("tenant-suspended").Returns(suspendedTenant);

            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = "tenant-suspended" };

            var act = () => middleware.BeforeAsync(envelope, accessor, setter, store);
            await act.Should().ThrowAsync<InvalidOperationException>();

            middleware.Finally(setter);

            logger.Information("After exception");
            sink.Events.Should().HaveCount(1);
            sink.Events[0].Properties.Should().NotContainKey("TenantId");
        }
    }

    [Fact]
    public async Task BeforeAsync_WhenTenantNotFound_ThrowsAndDoesNotEnrichLogContext()
    {
        var (logger, sink) = CreateLoggerWithSink();
        using (logger)
        {
            var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
            accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null));

            var setter = Substitute.For<IMultiTenantContextSetter>();
            var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();
            store.GetByIdentifierAsync("unknown").Returns((ExamPrismTenantInfo?)null);

            var middleware = new TenantWolverineMiddleware();
            var envelope = new Envelope { TenantId = "unknown" };

            var act = () => middleware.BeforeAsync(envelope, accessor, setter, store);
            await act.Should().ThrowAsync<InvalidOperationException>();

            middleware.Finally(setter);

            logger.Information("After exception");
            sink.Events.Should().HaveCount(1);
            sink.Events[0].Properties.Should().NotContainKey("TenantId");
        }
    }

    [Fact]
    public void Finally_WhenCalledMultipleTimesOrWithoutBeforeAsync_DoesNotThrow()
    {
        var middleware = new TenantWolverineMiddleware();
        var act = () =>
        {
            middleware.Finally();
            middleware.Finally();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public async Task BeforeAsync_WhenArgumentsAreNull_ThrowsArgumentNullException()
    {
        var middleware = new TenantWolverineMiddleware();
        var envelope = new Envelope { TenantId = "test" };
        var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        var setter = Substitute.For<IMultiTenantContextSetter>();
        var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();

        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.BeforeAsync(null!, accessor, setter, store));
        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.BeforeAsync(envelope, null!, setter, store));
        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.BeforeAsync(envelope, accessor, null!, store));
        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.BeforeAsync(envelope, accessor, setter, null!));
    }
}
