namespace ExamPrism.Infrastructure.Logging;

using System;
using System.Threading.Tasks;
using Common.MultiTenancy;
using Common.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Context;

public sealed class LogEnrichmentMiddleware
{
    private readonly RequestDelegate _next;

    public LogEnrichmentMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tenantContext = context.RequestServices?.GetService<ITenantContext>();
        var userContext = context.RequestServices?.GetService<IUserContext>();

        var tenantId = tenantContext?.TenantId;
        var userId = userContext?.UserId;

        IDisposable? tenantToken = null;
        IDisposable? userToken = null;

        try
        {
            if (tenantId.HasValue && tenantId.Value != Guid.Empty)
            {
                tenantToken = LogContext.PushProperty("TenantId", tenantId.Value);
            }

            if (userId.HasValue && userId.Value != Guid.Empty)
            {
                userToken = LogContext.PushProperty("UserId", userId.Value);
            }

            await _next(context);
        }
        finally
        {
            try
            {
                userToken?.Dispose();
            }
            finally
            {
                tenantToken?.Dispose();
            }
        }
    }
}

public static class LogEnrichmentMiddlewareExtensions
{
    public static IApplicationBuilder UseLogEnrichment(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<LogEnrichmentMiddleware>();
    }

    public static void EnrichDiagnosticContext(IDiagnosticContext diagnosticContext, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(diagnosticContext);
        ArgumentNullException.ThrowIfNull(httpContext);

        var tenantContext = httpContext.RequestServices?.GetService<ITenantContext>();
        if (tenantContext?.TenantId is { } tenantId && tenantId != Guid.Empty)
        {
            diagnosticContext.Set("TenantId", tenantId);
        }

        var userContext = httpContext.RequestServices?.GetService<IUserContext>();
        if (userContext?.UserId is { } userId && userId != Guid.Empty)
        {
            diagnosticContext.Set("UserId", userId);
        }
    }
}
