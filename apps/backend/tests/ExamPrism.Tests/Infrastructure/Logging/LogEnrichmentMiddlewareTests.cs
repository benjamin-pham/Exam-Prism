namespace Tests.Infrastructure.Logging;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Common.MultiTenancy;
using Common.Security;
using ExamPrism.Infrastructure.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

public class LogEnrichmentMiddlewareTests
{
    private sealed class MemorySink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();

        public void Emit(LogEvent logEvent)
        {
            Events.Add(logEvent);
        }
    }

    [Fact]
    public void Constructor_WhenNextIsNull_ThrowsArgumentNullException()
    {
        var act = () => new LogEnrichmentMiddleware(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("next");
    }

    [Fact]
    public async Task InvokeAsync_WhenContextIsNull_ThrowsArgumentNullException()
    {
        var middleware = new LogEnrichmentMiddleware(_ => Task.CompletedTask);
        var act = () => middleware.InvokeAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("context");
    }

    [Fact]
    public async Task InvokeAsync_WhenTenantAndUserContextArePresent_EnrichesLogContextDuringNext_AndDisposesAfter()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(tenantId);

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(userId);

        var services = new ServiceCollection();
        services.AddSingleton(tenantContext);
        services.AddSingleton(userContext);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        var sink = new MemorySink();
        using var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var nextCalled = false;
        var middleware = new LogEnrichmentMiddleware(ctx =>
        {
            nextCalled = true;
            logger.Information("Inside pipeline");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(httpContext);

        nextCalled.Should().BeTrue();
        sink.Events.Should().HaveCount(1);

        var insideEvent = sink.Events[0];
        insideEvent.Properties.Should().ContainKey("TenantId");
        insideEvent.Properties["TenantId"].ToString().Should().Contain(tenantId.ToString());

        insideEvent.Properties.Should().ContainKey("UserId");
        insideEvent.Properties["UserId"].ToString().Should().Contain(userId.ToString());

        // Log outside after middleware finishes: LogContext should be clean
        logger.Information("Outside pipeline");
        sink.Events.Should().HaveCount(2);
        var outsideEvent = sink.Events[1];
        outsideEvent.Properties.Should().NotContainKey("TenantId");
        outsideEvent.Properties.Should().NotContainKey("UserId");
    }

    [Fact]
    public async Task InvokeAsync_WhenTenantAndUserContextAreNull_InvokesNextWithoutThrowingOrEnriching()
    {
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        var sink = new MemorySink();
        using var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var nextCalled = false;
        var middleware = new LogEnrichmentMiddleware(ctx =>
        {
            nextCalled = true;
            logger.Information("Inside pipeline with null contexts");
            return Task.CompletedTask;
        });

        var act = () => middleware.InvokeAsync(httpContext);
        await act.Should().NotThrowAsync();

        nextCalled.Should().BeTrue();
        sink.Events.Should().HaveCount(1);
        sink.Events[0].Properties.Should().NotContainKey("TenantId");
        sink.Events[0].Properties.Should().NotContainKey("UserId");
    }

    [Fact]
    public async Task InvokeAsync_WhenTenantAndUserContextAreEmptyGuids_DoesNotEnrichEmptyProperties()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(Guid.Empty);

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(Guid.Empty);

        var services = new ServiceCollection();
        services.AddSingleton(tenantContext);
        services.AddSingleton(userContext);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        var sink = new MemorySink();
        using var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var nextCalled = false;
        var middleware = new LogEnrichmentMiddleware(ctx =>
        {
            nextCalled = true;
            logger.Information("Inside pipeline with empty guids");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(httpContext);

        nextCalled.Should().BeTrue();
        sink.Events.Should().HaveCount(1);
        sink.Events[0].Properties.Should().NotContainKey("TenantId");
        sink.Events[0].Properties.Should().NotContainKey("UserId");
    }

    [Fact]
    public async Task InvokeAsync_WhenOnlyTenantIdIsPresent_EnrichesOnlyTenantId()
    {
        var tenantId = Guid.NewGuid();
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(tenantId);

        var services = new ServiceCollection();
        services.AddSingleton(tenantContext);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        var sink = new MemorySink();
        using var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var middleware = new LogEnrichmentMiddleware(ctx =>
        {
            logger.Information("Only tenant present");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(httpContext);

        sink.Events.Should().HaveCount(1);
        sink.Events[0].Properties.Should().ContainKey("TenantId");
        sink.Events[0].Properties["TenantId"].ToString().Should().Contain(tenantId.ToString());
        sink.Events[0].Properties.Should().NotContainKey("UserId");
    }

    [Fact]
    public async Task InvokeAsync_WhenOnlyUserIdIsPresent_EnrichesOnlyUserId()
    {
        var userId = Guid.NewGuid();
        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(userId);

        var services = new ServiceCollection();
        services.AddSingleton(userContext);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        var sink = new MemorySink();
        using var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var middleware = new LogEnrichmentMiddleware(ctx =>
        {
            logger.Information("Only user present");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(httpContext);

        sink.Events.Should().HaveCount(1);
        sink.Events[0].Properties.Should().ContainKey("UserId");
        sink.Events[0].Properties["UserId"].ToString().Should().Contain(userId.ToString());
        sink.Events[0].Properties.Should().NotContainKey("TenantId");
    }

    [Fact]
    public async Task InvokeAsync_WhenNextThrows_DisposesLogContextPropertiesAndRethrows()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(tenantId);

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(userId);

        var services = new ServiceCollection();
        services.AddSingleton(tenantContext);
        services.AddSingleton(userContext);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        var sink = new MemorySink();
        using var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var middleware = new LogEnrichmentMiddleware(ctx =>
        {
            logger.Information("Inside failing middleware");
            throw new InvalidOperationException("Downstream failure");
        });

        var act = () => middleware.InvokeAsync(httpContext);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Downstream failure");

        sink.Events.Should().HaveCount(1);
        sink.Events[0].Properties.Should().ContainKey("TenantId");
        sink.Events[0].Properties.Should().ContainKey("UserId");

        // After exception was thrown, LogContext must have been cleaned up
        logger.Information("After exception");
        sink.Events.Should().HaveCount(2);
        sink.Events[1].Properties.Should().NotContainKey("TenantId");
        sink.Events[1].Properties.Should().NotContainKey("UserId");
    }

    [Fact]
    public void EnrichDiagnosticContext_WhenTenantAndUserPresent_AttachesBothToDiagnosticContext()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(tenantId);

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(userId);

        var services = new ServiceCollection();
        services.AddSingleton(tenantContext);
        services.AddSingleton(userContext);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        var diagnosticContext = Substitute.For<IDiagnosticContext>();

        LogEnrichmentMiddlewareExtensions.EnrichDiagnosticContext(diagnosticContext, httpContext);

        diagnosticContext.Received(1).Set("TenantId", tenantId, false);
        diagnosticContext.Received(1).Set("UserId", userId, false);
    }

    [Fact]
    public void EnrichDiagnosticContext_WhenContextsAreNullOrEmpty_DoesNotSetProperties()
    {
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        var diagnosticContext = Substitute.For<IDiagnosticContext>();

        var act = () => LogEnrichmentMiddlewareExtensions.EnrichDiagnosticContext(diagnosticContext, httpContext);
        act.Should().NotThrow();

        diagnosticContext.DidNotReceiveWithAnyArgs().Set(default!, default!, default);
    }

    [Fact]
    public void EnrichDiagnosticContext_WhenGuidsAreEmpty_DoesNotSetProperties()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns(Guid.Empty);

        var userContext = Substitute.For<IUserContext>();
        userContext.UserId.Returns(Guid.Empty);

        var services = new ServiceCollection();
        services.AddSingleton(tenantContext);
        services.AddSingleton(userContext);
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        var diagnosticContext = Substitute.For<IDiagnosticContext>();

        LogEnrichmentMiddlewareExtensions.EnrichDiagnosticContext(diagnosticContext, httpContext);

        diagnosticContext.DidNotReceiveWithAnyArgs().Set(default!, default!, default);
    }
}
