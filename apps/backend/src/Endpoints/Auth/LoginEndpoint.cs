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

public sealed class LoginEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (
            LoginCommand command,
            IMessageBus bus,
            HttpContext httpContext,
            IOptions<JwtOptions> jwtOptions,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<LoginResult>>(command, ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            AuthCookieHelper.SetAuthCookies(
                httpContext.Response,
                result.Value.AccessToken,
                result.Value.RefreshToken,
                jwtOptions.Value,
                timeProvider);

            return Results.Ok(result.Value);
        })
        .WithTags("Authentication")
        .WithSummary("Authenticates user credentials and sets auth cookies")
        .RequireTenant()
        .RequireRateLimiting(RateLimitPolicies.Auth);
    }
}
