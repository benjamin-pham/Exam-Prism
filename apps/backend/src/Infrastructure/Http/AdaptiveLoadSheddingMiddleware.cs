namespace ExamPrism.Infrastructure.Http;

using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Common.Http;
using Common.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

public class AdaptiveLoadSheddingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly AdaptiveConcurrencyLimiter _limiter;
    private readonly LoadSheddingOptions _options;

    public AdaptiveLoadSheddingMiddleware(
        RequestDelegate next,
        AdaptiveConcurrencyLimiter limiter,
        IOptions<LoadSheddingOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        _options = options?.Value ?? new LoadSheddingOptions();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled || ShouldBypass(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var priority = GetRequestPriority(context);

        if (!_limiter.TryAcquire(priority, out var lease))
        {
            ExamPrismDiagnostics.RecordLoadSheddingRejection(priority.ToString());

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.Headers.RetryAfter = _options.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                context.Response.Headers["X-Load-Shed"] = "true";
                context.Response.ContentType = "application/problem+json";

                var problem = new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.6.4",
                    Title = "Service Unavailable",
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Detail = "The server is currently experiencing high load and cannot process your request. Please try again shortly.",
                    Instance = context.Request.Path.Value ?? string.Empty
                };

                var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
                problem.Extensions["traceId"] = traceId;

                await context.Response.WriteAsJsonAsync(
                    problem,
                    options: (JsonSerializerOptions?)null,
                    contentType: "application/problem+json",
                    cancellationToken: context.RequestAborted);
            }

            return;
        }

        ExamPrismDiagnostics.IncrementInFlightRequests();
        var startTimestamp = Stopwatch.GetTimestamp();
        var isServerError = false;

        try
        {
            using (lease)
            {
                try
                {
                    await _next(context);
                }
                catch
                {
                    isServerError = true;
                    throw;
                }
            }
        }
        finally
        {
            ExamPrismDiagnostics.DecrementInFlightRequests();
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            if (!isServerError)
            {
                isServerError = context.Response.StatusCode >= StatusCodes.Status500InternalServerError;
            }
            _limiter.RecordSample(elapsedMs, isServerError);
        }
    }

    private static RequestPriority GetRequestPriority(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var metadata = endpoint?.Metadata.GetMetadata<RequestPriorityMetadata>();
        if (metadata is not null)
        {
            return metadata.Priority;
        }

        if (context.Request.Headers.TryGetValue("X-Request-Priority", out var headerValue) &&
            Enum.TryParse<RequestPriority>(headerValue, ignoreCase: true, out var parsedPriority))
        {
            return parsedPriority;
        }

        return RequestPriority.Normal;
    }

    private static bool ShouldBypass(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        var value = path.Value;
        return value.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase);
    }
}

public static class AdaptiveLoadSheddingMiddlewareExtensions
{
    public static IApplicationBuilder UseAdaptiveLoadShedding(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AdaptiveLoadSheddingMiddleware>();
    }
}
