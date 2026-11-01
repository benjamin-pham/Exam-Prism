namespace ExamPrism.Endpoints.Auth;

using Common.Endpoints;
using Common.Results;
using ExamPrism.Features.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class LogoutEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/logout", async (
            HttpRequest request,
            HttpResponse response,
            HttpContext httpContext,
            IMessageBus bus,
            CancellationToken ct) =>
        {
            request.Cookies.TryGetValue(AuthCookieHelper.RefreshTokenCookieName, out var rawRefresh);
            var result = await bus.InvokeAsync<Result>(new LogoutCommand(rawRefresh), ct);
            AuthCookieHelper.ClearAuthCookies(response);

            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.NoContent();
        })
        .WithTags("Authentication")
        .WithSummary("Revokes refresh token and clears auth cookies");
    }
}
