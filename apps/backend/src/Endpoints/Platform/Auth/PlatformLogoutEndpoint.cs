namespace ExamPrism.Endpoints.Platform.Auth;

using Common.Endpoints;
using Common.Results;
using ExamPrism.Endpoints.Auth;
using ExamPrism.Features.Platform.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class PlatformLogoutEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/platform/auth/logout", async (
            HttpRequest request,
            HttpResponse response,
            HttpContext httpContext,
            IMessageBus bus,
            CancellationToken ct) =>
        {
            request.Cookies.TryGetValue(AuthCookieHelper.RefreshTokenCookieName, out var rawRefresh);
            var result = await bus.InvokeAsync<Result>(new PlatformLogoutCommand(rawRefresh), ct);
            AuthCookieHelper.ClearAuthCookies(response);

            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.NoContent();
        })
        .WithTags("Platform Authentication")
        .WithSummary("Revokes platform refresh token and clears auth cookies")
        .RequirePlatform();
    }
}
