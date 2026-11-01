namespace ExamPrism.Endpoints.Auth;

using Common.Endpoints;
using ExamPrism.Features.Auth.Refresh;
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
        app.MapPost("/api/auth/refresh", Handle)
            .WithTags("Authentication")
            .WithSummary("Rotates refresh token and returns updated auth cookies");
    }

    private static async Task<IResult> Handle(
        HttpRequest request,
        HttpResponse response,
        IMessageBus bus,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        if (!request.Cookies.TryGetValue(AuthCookieHelper.RefreshTokenCookieName, out var rawRefresh) ||
            string.IsNullOrWhiteSpace(rawRefresh))
        {
            return Results.Unauthorized();
        }

        var result = await bus.InvokeAsync<RefreshTokenResult?>(new RefreshTokenCommand(rawRefresh), ct);
        if (result is null)
        {
            AuthCookieHelper.ClearAuthCookies(response);
            return Results.Unauthorized();
        }

        AuthCookieHelper.SetAuthCookies(response, result.AccessToken, result.RefreshToken, jwtOptions.Value, timeProvider);
        return Results.Ok();
    }
}

