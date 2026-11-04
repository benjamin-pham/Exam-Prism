namespace ExamPrism.Infrastructure.Security;

using System;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Common.Security;
using Common.Telemetry;
using ExamPrism.Infrastructure.Http;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public sealed class IpPenaltyBoxMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IIpReputationService _reputationService;
    private readonly IOptions<DdosProtectionOptions> _options;
    private readonly IOptions<JwtOptions>? _jwtOptions;
    private readonly IOptions<ReverseProxyOptions>? _proxyOptions;

    public IpPenaltyBoxMiddleware(
        RequestDelegate next,
        IIpReputationService reputationService,
        IOptions<DdosProtectionOptions> options,
        IOptions<JwtOptions>? jwtOptions = null,
        IOptions<ReverseProxyOptions>? proxyOptions = null)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _reputationService = reputationService ?? throw new ArgumentNullException(nameof(reputationService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jwtOptions = jwtOptions;
        _proxyOptions = proxyOptions;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var opt = _options.Value;
        if (!opt.Enabled)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/metrics", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var clientIp = ReverseProxySetup.ResolveClientIp(context, _proxyOptions?.Value);
        var banRemaining = await _reputationService.GetBanRemainingAsync(clientIp, context.RequestAborted);

        if (banRemaining.HasValue)
        {
            var jwtOpt = _jwtOptions?.Value ?? context.RequestServices?.GetService<IOptions<JwtOptions>>()?.Value;

            if (opt.BypassPenaltyBoxForAuthenticatedJwt && jwtOpt is not null && HasValidJwtSignature(context, jwtOpt))
            {
                // Authenticated user with valid signature bypasses IP-level penalty box (School NAT room protection)
                await _next(context);
                return;
            }

            ExamPrismDiagnostics.RecordIpPenaltyBoxRejection();

            var retrySeconds = Math.Max(1, (int)Math.Ceiling(banRemaining.Value.TotalSeconds));
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.ContentType = "application/problem+json";
            context.Response.Headers.RetryAfter = retrySeconds.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-Penalty-Box"] = "true";

            var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
            var problem = new ProblemDetails
            {
                Type = "https://tools.ietf.org/html/rfc6585#section-4",
                Title = "Too Many Requests",
                Status = StatusCodes.Status429TooManyRequests,
                Detail = $"Your IP address has been temporarily restricted due to suspicious activity. Retry in {retrySeconds} seconds.",
                Instance = path
            };
            problem.Extensions["traceId"] = traceId;
            problem.Extensions["errorCode"] = "IP_PENALTY_BOX_BANNED";

            await context.Response.WriteAsJsonAsync(problem, options: (JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
            return;
        }

        await _next(context);

        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized &&
            (path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase) ||
             path.StartsWith("/api/platform/auth", StringComparison.OrdinalIgnoreCase)))
        {
            await _reputationService.RecordStrikeAsync(clientIp, IpStrikeReason.UnauthorizedAuthAttempt, cancellationToken: context.RequestAborted);
        }
    }

    public static bool HasValidJwtSignature(HttpContext context, JwtOptions jwtOptions)
    {
        if (jwtOptions is null || string.IsNullOrWhiteSpace(jwtOptions.SecretKey))
        {
            return false;
        }

        string? rawToken = null;
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var headerStr = authHeader.ToString();
            if (headerStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                rawToken = headerStr["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(rawToken) &&
            context.Request.Cookies.TryGetValue("access_token", out var cookieToken) &&
            !string.IsNullOrWhiteSpace(cookieToken))
        {
            rawToken = cookieToken.Trim();
        }

        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return false;
        }

        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            if (!tokenHandler.CanReadToken(rawToken))
            {
                return false;
            }

            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SecretKey)),
                ValidateIssuer = !string.IsNullOrWhiteSpace(jwtOptions.Issuer),
                ValidIssuer = jwtOptions.Issuer,
                ValidateAudience = !string.IsNullOrWhiteSpace(jwtOptions.Audience),
                ValidAudience = jwtOptions.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            var principal = tokenHandler.ValidateToken(rawToken, validationParameters, out var validatedToken);
            return principal is not null && validatedToken is not null;
        }
        catch
        {
            return false;
        }
    }
}

public static class IpPenaltyBoxMiddlewareExtensions
{
    public static IApplicationBuilder UseIpPenaltyBox(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<IpPenaltyBoxMiddleware>();
    }
}

