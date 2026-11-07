namespace ExamPrism.Endpoints.Auth;

using Common.Endpoints;
using Common.RateLimiting;
using Common.Results;
using ExamPrism.Features.Auth;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Wolverine;

public sealed class RefreshEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/refresh", async (
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

            var result = await bus.InvokeAsync<Result<RefreshTokenResult>>(new RefreshTokenCommand(rawRefresh), ct);
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
        .WithTags("Authentication")
        .WithSummary("Rotates refresh token and returns updated auth cookies")
        .RequireTenant()
        .RequireRateLimiting(RateLimitPolicies.Auth);
    }
}
