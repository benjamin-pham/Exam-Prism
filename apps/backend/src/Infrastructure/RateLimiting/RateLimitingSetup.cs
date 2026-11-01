namespace ExamPrism.Infrastructure.RateLimiting;

using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Common.Localization;
using Common.RateLimiting;
using ExamPrism.Infrastructure.Caching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

public static class RateLimitingSetup
{
    public static IServiceCollection AddExamPrismRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useRedis)
    {
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));
        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));

        services.AddRateLimiter();

        services.AddOptions<RateLimiterOptions>().Configure<IServiceProvider>((options, sp) =>
        {
            var rateLimitingOptions = sp.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
            var cacheOptions = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var multiplexer = sp.GetService<IConnectionMultiplexer>();
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var redisLogger = loggerFactory?.CreateLogger<RedisFixedWindowRateLimiter>()
                ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisFixedWindowRateLimiter>.Instance;

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.ContentType = "application/problem+json";

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                    context.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                }

                var localizer = context.HttpContext.RequestServices.GetService<IStringLocalizer<SharedResources>>();
                var detail = localizer?["RateLimitExceeded"]?.Value ?? "Too many requests. Please try again later.";

                var problem = new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc6585#section-4",
                    Title = "Too Many Requests",
                    Status = StatusCodes.Status429TooManyRequests,
                    Detail = detail,
                    Instance = context.HttpContext.Request.Path.Value ?? string.Empty
                };

                var traceId = Activity.Current?.Id ?? context.HttpContext.TraceIdentifier;
                problem.Extensions["traceId"] = traceId;

                await context.HttpContext.Response.WriteAsJsonAsync(problem, options: (JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: cancellationToken);
            };

            options.AddPolicy(RateLimitPolicies.Auth, httpContext =>
            {
                var ip = GetClientIp(httpContext);
                var partitionKey = $"auth:ip:{ip}";

                return RateLimitPartition.Get(partitionKey, _ => useRedis && multiplexer is not null
                    ? new RedisFixedWindowRateLimiter(
                        multiplexer,
                        $"{cacheOptions.KeyPrefix}ratelimit:{partitionKey}",
                        rateLimitingOptions.Auth.PermitLimit,
                        TimeSpan.FromSeconds(rateLimitingOptions.Auth.WindowSeconds),
                        timeProvider,
                        redisLogger)
                    : new InMemoryFixedWindowRateLimiter(
                        rateLimitingOptions.Auth.PermitLimit,
                        TimeSpan.FromSeconds(rateLimitingOptions.Auth.WindowSeconds),
                        timeProvider));
            });

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                var endpoint = httpContext.GetEndpoint();
                if (endpoint is not null)
                {
                    if (endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null)
                    {
                        return RateLimitPartition.GetNoLimiter("infra");
                    }

                    if (endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>() is not null)
                    {
                        return RateLimitPartition.GetNoLimiter("endpoint-policy");
                    }
                }

                var path = httpContext.Request.Path.Value ?? string.Empty;
                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/health/", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/openapi", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/openapi/", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/scalar", StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith("/scalar/", StringComparison.OrdinalIgnoreCase))
                {
                    return RateLimitPartition.GetNoLimiter("infra");
                }

                string partitionKey;
                if (httpContext.User.Identity?.IsAuthenticated == true)
                {
                    var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        ?? httpContext.User.FindFirst("sub")?.Value;

                    partitionKey = !string.IsNullOrWhiteSpace(userId)
                        ? $"global:user:{userId}"
                        : $"global:ip:{GetClientIp(httpContext)}";
                }
                else
                {
                    partitionKey = $"global:ip:{GetClientIp(httpContext)}";
                }

                return RateLimitPartition.Get(partitionKey, _ => useRedis && multiplexer is not null
                    ? new RedisFixedWindowRateLimiter(
                        multiplexer,
                        $"{cacheOptions.KeyPrefix}ratelimit:{partitionKey}",
                        rateLimitingOptions.Global.PermitLimit,
                        TimeSpan.FromSeconds(rateLimitingOptions.Global.WindowSeconds),
                        timeProvider,
                        redisLogger)
                    : new InMemoryFixedWindowRateLimiter(
                        rateLimitingOptions.Global.PermitLimit,
                        TimeSpan.FromSeconds(rateLimitingOptions.Global.WindowSeconds),
                        timeProvider));
            });
        });

        return services;
    }

    private static string GetClientIp(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
