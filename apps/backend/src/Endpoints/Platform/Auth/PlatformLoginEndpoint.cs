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

public sealed class PlatformLoginEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/platform/auth/login", async (
            PlatformLoginCommand command,
            IMessageBus bus,
            HttpContext httpContext,
            IOptions<JwtOptions> jwtOptions,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<PlatformLoginResult>>(command, ct);
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
        .WithTags("Platform Authentication")
        .WithSummary("Authenticates platform administrator credentials and sets auth cookies")
        .RequirePlatform()
        .RequireRateLimiting(RateLimitPolicies.Auth);
    }
}
