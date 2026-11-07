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

public sealed class RegisterEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/register", async (
            RegisterCommand command,
            IMessageBus bus,
            HttpContext httpContext,
            IOptions<JwtOptions> jwtOptions,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<RegisterResult>>(command, ct);
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

            return Results.Created("/api/auth/me", result.Value);
        })
        .WithTags("Authentication")
        .WithSummary("Registers a new user and sets auth cookies")
        .RequireTenant()
        .RequireRateLimiting(RateLimitPolicies.Auth);
    }
}
