namespace ExamPrism.Infrastructure.Http;

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Common.Exceptions;
using Common.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public sealed class DecompressionBombGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IOptions<RuntimeTuningOptions> _tuningOptions;
    private readonly IIpReputationService? _reputationService;
    private readonly IOptions<ReverseProxyOptions>? _proxyOptions;

    public DecompressionBombGuardMiddleware(
        RequestDelegate next,
        IOptions<RuntimeTuningOptions> tuningOptions,
        IIpReputationService? reputationService = null,
        IOptions<ReverseProxyOptions>? proxyOptions = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _tuningOptions = tuningOptions ?? throw new ArgumentNullException(nameof(tuningOptions));
        _reputationService = reputationService;
        _proxyOptions = proxyOptions;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var maxAllowedBytes = _tuningOptions.Value.MaxDecompressedBodySizeBytes;
        if (maxAllowedBytes <= 0)
        {
            await _next(context);
            return;
        }

        var originalBody = context.Request.Body;
        var boundedStream = new BoundedDecompressedReadStream(originalBody, maxAllowedBytes);
        context.Request.Body = boundedStream;

        try
        {
            await _next(context);
        }
        catch (PayloadTooLargeDecompressionException ex)
        {
            var repService = _reputationService ?? context.RequestServices?.GetService<IIpReputationService>();
            var clientIp = ReverseProxySetup.ResolveClientIp(context, _proxyOptions?.Value);

            if (repService is not null)
            {
                await repService.RecordStrikeAsync(clientIp, IpStrikeReason.DecompressionBomb, cancellationToken: context.RequestAborted);
            }

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                context.Response.ContentType = "application/problem+json";

                var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
                var problem = new ProblemDetails
                {
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.11",
                    Title = "Payload Too Large",
                    Status = StatusCodes.Status413PayloadTooLarge,
                    Detail = ex.Message,
                    Instance = context.Request.Path.Value ?? string.Empty
                };
                problem.Extensions["traceId"] = traceId;
                problem.Extensions["errorCode"] = "DECOMPRESSION_BOMB_DETECTED";

                await context.Response.WriteAsJsonAsync(problem, options: (JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
                return;
            }

            throw;
        }
        finally
        {
            context.Request.Body = originalBody;
        }
    }
}

public static class DecompressionBombGuardMiddlewareExtensions
{
    public static IApplicationBuilder UseDecompressionBombGuard(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<DecompressionBombGuardMiddleware>();
    }
}

