namespace ExamPrism.Endpoints.Auth;

using Common.Endpoints;
using ExamPrism.Features.Auth.Logout;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class LogoutEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/logout", Handle)
            .WithTags("Authentication")
            .WithSummary("Revokes refresh token and clears auth cookies");
    }

    private static async Task<IResult> Handle(
        HttpRequest request,
        HttpResponse response,
        IMessageBus bus,
        CancellationToken ct)
    {
        request.Cookies.TryGetValue(AuthCookieHelper.RefreshTokenCookieName, out var rawRefresh);
        await bus.InvokeAsync(new LogoutCommand(rawRefresh), ct);
        AuthCookieHelper.ClearAuthCookies(response);
        return Results.NoContent();
    }
}
