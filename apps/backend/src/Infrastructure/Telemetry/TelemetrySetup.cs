namespace ExamPrism.Infrastructure.Telemetry;

using System.Text;
using Common.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

public static class TelemetrySetup
{
    public static IServiceCollection AddAppTelemetry(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<TelemetryOptions>(configuration.GetSection(TelemetryOptions.SectionName));
        var telemetryOptions = configuration.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>()
            ?? new TelemetryOptions();

        var otelBuilder = services.AddOpenTelemetry();

        otelBuilder.WithTracing(tracing =>
        {
            tracing
                .AddSource(
                    ExamPrismDiagnostics.ActivitySourceName,
                    "Microsoft.Orleans.Runtime",
                    "Microsoft.Orleans.Application",
                    "Wolverine",
                    "Npgsql")
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.Filter = httpContext =>
                        !httpContext.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) &&
                        !httpContext.Request.Path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);
                })
                .AddHttpClientInstrumentation();

            if (!string.IsNullOrWhiteSpace(telemetryOptions.OtlpEndpoint) &&
                Uri.TryCreate(telemetryOptions.OtlpEndpoint, UriKind.Absolute, out var endpointUri))
            {
                tracing.AddOtlpExporter(otlp => otlp.Endpoint = endpointUri);
            }
        });

        otelBuilder.WithMetrics(metrics =>
        {
            metrics
                .AddMeter(
                    ExamPrismDiagnostics.MeterName,
                    "Microsoft.Orleans",
                    "Npgsql",
                    "Microsoft.AspNetCore.RateLimiting")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation();

            if (!string.IsNullOrWhiteSpace(telemetryOptions.OtlpEndpoint) &&
                Uri.TryCreate(telemetryOptions.OtlpEndpoint, UriKind.Absolute, out var endpointUri))
            {
                metrics.AddOtlpExporter(otlp => otlp.Endpoint = endpointUri);
            }
        });

        return services;
    }

    public static IApplicationBuilder UseAppTelemetry(this IApplicationBuilder app)
    {
        var telemetryOptions = app.ApplicationServices.GetService<IOptions<TelemetryOptions>>()?.Value
            ?? new TelemetryOptions();

        if (!telemetryOptions.EnablePrometheusEndpoint)
        {
            return app;
        }

        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            {
                if (context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase) ||
                    context.Request.Path.Equals("/metrics/", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";

                    if (HttpMethods.IsHead(context.Request.Method))
                    {
                        return;
                    }

                    var builder = new StringBuilder();
                    builder.AppendLine("# HELP examprism_db_slow_queries_total Number of database queries exceeding the slow query threshold");
                    builder.AppendLine("# TYPE examprism_db_slow_queries_total counter");
                    builder.AppendLine($"examprism_db_slow_queries_total {ExamPrismDiagnostics.SlowQueriesCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_cache_backplane_evictions_total Number of L1 cache evictions triggered by Redis backplane");
                    builder.AppendLine("# TYPE examprism_cache_backplane_evictions_total counter");
                    builder.AppendLine($"examprism_cache_backplane_evictions_total {ExamPrismDiagnostics.CacheBackplaneEvictionsCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_idempotency_replays_total Number of idempotent requests served from cache");
                    builder.AppendLine("# TYPE examprism_idempotency_replays_total counter");
                    builder.AppendLine($"examprism_idempotency_replays_total {ExamPrismDiagnostics.IdempotencyReplaysCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_idempotency_conflicts_total Number of concurrent idempotent requests rejected due to in-flight locks");
                    builder.AppendLine("# TYPE examprism_idempotency_conflicts_total counter");
                    builder.AppendLine($"examprism_idempotency_conflicts_total {ExamPrismDiagnostics.IdempotencyConflictsCount}");
                    builder.AppendLine();

                    await context.Response.WriteAsync(builder.ToString(), Encoding.UTF8);
                    return;
                }
            }

            await next();
        });

        return app;
    }
}
