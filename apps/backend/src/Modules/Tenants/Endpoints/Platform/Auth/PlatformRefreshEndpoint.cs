namespace ExamPrism.Endpoints.Platform.Auth;

using Common.Endpoints;
using Common.RateLimiting;
using Common.Results;
using ExamPrism.Endpoints.Auth;
using ExamPrism.Features.Platform.Auth;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Wolverine;

public sealed class PlatformRefreshEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/platform/auth/refresh", async (
            HttpRequest request,
            HttpResponse response,
            HttpContext httpContext,
            IMessageBus bus,
            IOptions<JwtOptions> jwtOptions,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            if (!request.Cookies.TryGetValue(AuthCookieHelper.RefreshTokenCookieName, out var rawRefresh) ||
                string.IsNullOrWhiteSpace(rawRefresh))
            {
                return Results.Unauthorized();
            }

            var result = await bus.InvokeAsync<Result<PlatformRefreshTokenResult>>(new PlatformRefreshTokenCommand(rawRefresh), ct);
            if (result.IsFailure)
            {
                AuthCookieHelper.ClearAuthCookies(response);
                return result.ToProblemResult(httpContext);
            }

            AuthCookieHelper.SetAuthCookies(
                response,
                result.Value.AccessToken,
                result.Value.RefreshToken,
                jwtOptions.Value,
                timeProvider);

            return Results.Ok();
        })
        .WithTags("Platform Authentication")
        .WithSummary("Rotates platform refresh token and returns updated auth cookies")
        .RequirePlatform()
        .RequireRateLimiting(RateLimitPolicies.Auth);
    }
}
