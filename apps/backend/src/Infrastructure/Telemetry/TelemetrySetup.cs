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

        var rawOtlpEndpoint = !string.IsNullOrWhiteSpace(telemetryOptions.OtlpEndpoint)
            ? telemetryOptions.OtlpEndpoint
            : configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        Uri? otlpEndpointUri = null;
        var hasOtlpEndpoint = !string.IsNullOrWhiteSpace(rawOtlpEndpoint) &&
            Uri.TryCreate(rawOtlpEndpoint, UriKind.Absolute, out otlpEndpointUri);

        var otelBuilder = services.AddOpenTelemetry();

        otelBuilder.WithTracing(tracing =>
        {
            tracing
                .SetSampler(new AdaptiveTraceSampler(telemetryOptions.TraceSamplingRatio))
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

            if (hasOtlpEndpoint && otlpEndpointUri is not null)
            {
                tracing.AddOtlpExporter(otlp => otlp.Endpoint = otlpEndpointUri);
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

            if (hasOtlpEndpoint && otlpEndpointUri is not null)
            {
                metrics.AddOtlpExporter(otlp => otlp.Endpoint = otlpEndpointUri);
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
                    builder.AppendLine("# HELP examprism_load_shedding_rejected_total Number of requests rejected by adaptive load shedding");
                    builder.AppendLine("# TYPE examprism_load_shedding_rejected_total counter");
                    builder.AppendLine($"examprism_load_shedding_rejected_total {ExamPrismDiagnostics.LoadSheddingRejectedCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_auth_jwt_revoked_rejected_total Number of authentication rejections due to revoked JWT");
                    builder.AppendLine("# TYPE examprism_auth_jwt_revoked_rejected_total counter");
                    builder.AppendLine($"examprism_auth_jwt_revoked_rejected_total {ExamPrismDiagnostics.JwtRevokedRejectedCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_auth_password_hash_bulkhead_rejected_total Number of password hashing requests rejected by CPU bulkhead");
                    builder.AppendLine("# TYPE examprism_auth_password_hash_bulkhead_rejected_total counter");
                    builder.AppendLine($"examprism_auth_password_hash_bulkhead_rejected_total {ExamPrismDiagnostics.PasswordHashBulkheadRejectedCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_batcher_flushed_items_total Number of items flushed by write-behind batchers");
                    builder.AppendLine("# TYPE examprism_batcher_flushed_items_total counter");
                    builder.AppendLine($"examprism_batcher_flushed_items_total {ExamPrismDiagnostics.BatcherFlushedItemsCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_db_tenant_datasources_active Number of active cached tenant NpgsqlDataSources");
                    builder.AppendLine("# TYPE examprism_db_tenant_datasources_active gauge");
                    builder.AppendLine($"examprism_db_tenant_datasources_active {ExamPrismDiagnostics.ActiveTenantDataSourcesCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_http_in_flight_requests Current number of in-flight HTTP requests");
                    builder.AppendLine("# TYPE examprism_http_in_flight_requests gauge");
                    builder.AppendLine($"examprism_http_in_flight_requests {ExamPrismDiagnostics.InFlightRequestsCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_security_ip_penalty_box_rejected_total Number of requests rejected by IP penalty box");
                    builder.AppendLine("# TYPE examprism_security_ip_penalty_box_rejected_total counter");
                    builder.AppendLine($"examprism_security_ip_penalty_box_rejected_total {ExamPrismDiagnostics.IpPenaltyBoxRejectedCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_security_ip_bans_issued_total Number of IP bans issued by reputation service");
                    builder.AppendLine("# TYPE examprism_security_ip_bans_issued_total counter");
                    builder.AppendLine($"examprism_security_ip_bans_issued_total {ExamPrismDiagnostics.IpBansIssuedCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_security_decompression_bomb_rejected_total Number of requests rejected due to decompression bomb detection");
                    builder.AppendLine("# TYPE examprism_security_decompression_bomb_rejected_total counter");
                    builder.AppendLine($"examprism_security_decompression_bomb_rejected_total {ExamPrismDiagnostics.DecompressionBombRejectedCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_security_tenant_catalog_shield_rejected_total Number of requests rejected by tenant catalog cold-miss shield");
                    builder.AppendLine("# TYPE examprism_security_tenant_catalog_shield_rejected_total counter");
                    builder.AppendLine($"examprism_security_tenant_catalog_shield_rejected_total {ExamPrismDiagnostics.TenantCatalogShieldRejectedCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_security_spoofed_ip_ignored_total Number of spoofed forwarded IP headers ignored from untrusted peers");
                    builder.AppendLine("# TYPE examprism_security_spoofed_ip_ignored_total counter");
                    builder.AppendLine($"examprism_security_spoofed_ip_ignored_total {ExamPrismDiagnostics.SpoofedIpIgnoredCount}");
                    builder.AppendLine();
                    builder.AppendLine("# HELP examprism_http_rate_limit_rejected_total Number of requests rejected by rate limiting");
                    builder.AppendLine("# TYPE examprism_http_rate_limit_rejected_total counter");
                    builder.AppendLine($"examprism_http_rate_limit_rejected_total {ExamPrismDiagnostics.RateLimitRejectedCount}");
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
