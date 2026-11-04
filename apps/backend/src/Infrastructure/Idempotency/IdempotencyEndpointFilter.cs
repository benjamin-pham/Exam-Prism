namespace ExamPrism.Infrastructure.Idempotency;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Common.Caching;
using Common.Idempotency;
using Common.Locking;
using Common.MultiTenancy;
using Common.Telemetry;
using ExamPrism.Infrastructure.Http;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

public sealed class IdempotencyCacheEntry
{
    public string PayloadHash { get; set; } = string.Empty;
    public int StatusCode { get; set; } = StatusCodes.Status200OK;
    public string? ContentType { get; set; } = "application/json";
    public string? ResponseBody { get; set; }
}

public sealed class IdempotencyEndpointFilter : IIdempotencyFilter
{
    private readonly ICacheService _cache;
    private readonly IDistributedLock _lock;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<IdempotencyEndpointFilter> _logger;

    public IdempotencyEndpointFilter(
        ICacheService cache,
        IDistributedLock @lock,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyEndpointFilter>? logger = null)
        : this(cache, @lock, options.Value, logger)
    {
    }

    public IdempotencyEndpointFilter(
        ICacheService cache,
        IDistributedLock @lock,
        IdempotencyOptions? options = null,
        ILogger<IdempotencyEndpointFilter>? logger = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _lock = @lock ?? throw new ArgumentNullException(nameof(@lock));
        _options = options ?? new IdempotencyOptions();
        _logger = logger ?? NullLogger<IdempotencyEndpointFilter>.Instance;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var method = httpContext.Request.Method;

        if (!HttpMethods.IsPost(method) && !HttpMethods.IsPut(method) && !HttpMethods.IsPatch(method))
        {
            return await next(context);
        }

        if (!httpContext.Request.Headers.TryGetValue(_options.HeaderName, out var headerValues) ||
            StringValues.IsNullOrEmpty(headerValues))
        {
            return await next(context);
        }

        var idempotencyKey = headerValues.ToString().Trim();
        if (string.IsNullOrEmpty(idempotencyKey))
        {
            return await next(context);
        }

        if (idempotencyKey.Length > 128)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            var problem = new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                Title = "Bad Request",
                Status = StatusCodes.Status400BadRequest,
                Detail = "Idempotency-Key header length must be between 1 and 128 characters.",
                Instance = httpContext.Request.Path.Value ?? string.Empty
            };
            return Results.Problem(problem);
        }

        httpContext.Request.EnableBuffering();
        string bodyHash;
        if (httpContext.Request.Body.CanSeek)
        {
            httpContext.Request.Body.Position = 0;
        }

        using (var sha256 = SHA256.Create())
        {
            var hashBytes = await sha256.ComputeHashAsync(httpContext.Request.Body, httpContext.RequestAborted);
            bodyHash = Convert.ToHexStringLower(hashBytes);
        }

        if (httpContext.Request.Body.CanSeek)
        {
            httpContext.Request.Body.Position = 0;
        }

        var tenantInfo = httpContext.GetMultiTenantContext<ExamPrismTenantInfo>()?.TenantInfo;
        var scope = tenantInfo is { Status: TenantStatus.Active } ? $"t:{tenantInfo.Id}" : "platform";

        string userOrIp;
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? httpContext.User.FindFirst("sub")?.Value;
            userOrIp = !string.IsNullOrWhiteSpace(userId) ? $"user:{userId}" : $"ip:{ReverseProxySetup.ResolveClientIp(httpContext)}";
        }
        else
        {
            userOrIp = $"ip:{ReverseProxySetup.ResolveClientIp(httpContext)}";
        }

        var normalizedMethod = method.ToUpperInvariant();
        var path = httpContext.Request.Path.Value ?? string.Empty;
        var cacheKey = $"idempotency:{scope}:{userOrIp}:{normalizedMethod}:{path}:{idempotencyKey}";

        var cached = await _cache.GetOrDefaultAsync<IdempotencyCacheEntry>(cacheKey, httpContext.RequestAborted);
        if (cached is not null)
        {
            if (cached.PayloadHash != bodyHash)
            {
                httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                var problem = new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc4918#section-11.2",
                    Title = "Unprocessable Entity",
                    Status = StatusCodes.Status422UnprocessableEntity,
                    Detail = "Idempotency-Key has already been used with a different request payload.",
                    Instance = path
                };
                return Results.Problem(problem);
            }

            httpContext.Response.StatusCode = cached.StatusCode;
            httpContext.Response.Headers["X-Idempotency-Replayed"] = "true";
            ExamPrismDiagnostics.RecordIdempotencyReplay();
            return new IdempotencyReplayResult(cached.StatusCode, cached.ResponseBody, cached.ContentType);
        }

        var lockTimeout = TimeSpan.FromSeconds(_options.LockTimeoutSeconds);
        var lockHandle = await _lock.TryAcquireAsync(cacheKey, lockTimeout, httpContext.RequestAborted);
        if (lockHandle is null)
        {
            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
            httpContext.Response.Headers.RetryAfter = "1";
            ExamPrismDiagnostics.RecordIdempotencyConflict();
            var problem = new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.8",
                Title = "Conflict",
                Status = StatusCodes.Status409Conflict,
                Detail = "A request with the same Idempotency-Key is currently being processed.",
                Instance = path
            };
            return Results.Problem(problem);
        }

        await using (lockHandle)
        {
            var secondCheck = await _cache.GetOrDefaultAsync<IdempotencyCacheEntry>(cacheKey, httpContext.RequestAborted);
            if (secondCheck is not null)
            {
                if (secondCheck.PayloadHash != bodyHash)
                {
                    httpContext.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                    var problem = new ProblemDetails
                    {
                        Type = "https://tools.ietf.org/html/rfc4918#section-11.2",
                        Title = "Unprocessable Entity",
                        Status = StatusCodes.Status422UnprocessableEntity,
                        Detail = "Idempotency-Key has already been used with a different request payload.",
                        Instance = path
                    };
                    return Results.Problem(problem);
                }

                httpContext.Response.StatusCode = secondCheck.StatusCode;
                httpContext.Response.Headers["X-Idempotency-Replayed"] = "true";
                ExamPrismDiagnostics.RecordIdempotencyReplay();
                return new IdempotencyReplayResult(secondCheck.StatusCode, secondCheck.ResponseBody, secondCheck.ContentType);
            }

            var result = await next(context);

            var statusCode = StatusCodes.Status200OK;
            if (result is IStatusCodeHttpResult statusResult && statusResult.StatusCode.HasValue)
            {
                statusCode = statusResult.StatusCode.Value;
            }
            else if (httpContext.Response.StatusCode != 0)
            {
                statusCode = httpContext.Response.StatusCode;
            }

            if (statusCode is >= 200 and < 300)
            {
                string? responseBody = null;
                if (result is IValueHttpResult valueResult)
                {
                    responseBody = valueResult.Value is not null ? JsonSerializer.Serialize(valueResult.Value) : null;
                }
                else if (result is not IResult and not null)
                {
                    responseBody = JsonSerializer.Serialize(result);
                }
                else if (result is IdempotencyReplayResult replay)
                {
                    responseBody = replay.ResponseBody;
                }

                var entry = new IdempotencyCacheEntry
                {
                    PayloadHash = bodyHash,
                    StatusCode = statusCode,
                    ContentType = "application/json",
                    ResponseBody = responseBody
                };

                await _cache.SetAsync(
                    cacheKey,
                    entry,
                    expiration: TimeSpan.FromMinutes(_options.ExpirationMinutes),
                    cancellationToken: httpContext.RequestAborted);
            }

            return result;
        }
    }
}

public sealed class IdempotencyReplayResult : IResult, IStatusCodeHttpResult, IContentTypeHttpResult, IValueHttpResult
{
    public int? StatusCode { get; }
    public string? ContentType { get; }
    public string? ResponseBody { get; }
    public object? Value { get; }

    public IdempotencyReplayResult(int statusCode, string? responseBody, string? contentType = "application/json")
    {
        StatusCode = statusCode;
        ContentType = contentType ?? "application/json";
        ResponseBody = responseBody;
        Value = responseBody;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCode ?? StatusCodes.Status200OK;
        httpContext.Response.Headers["X-Idempotency-Replayed"] = "true";
        if (ContentType is not null)
        {
            httpContext.Response.ContentType = ContentType;
        }
        if (ResponseBody is not null)
        {
            await httpContext.Response.WriteAsync(ResponseBody);
        }
    }
}
