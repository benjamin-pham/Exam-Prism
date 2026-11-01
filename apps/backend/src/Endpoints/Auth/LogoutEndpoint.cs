namespace ExamPrism.Endpoints.Auth;

using Common.Endpoints;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

public sealed class LogoutEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/logout", HandleAsync)
            .WithTags("Authentication")
            .WithName("Logout")
            .WithDescription("Revokes refresh token and clears auth cookies");
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        HttpResponse response,
        AppDbContext db,
        ITokenService tokenService,
        TimeProvider timeProvider)
    {
        if (request.Cookies.TryGetValue(AuthCookieHelper.RefreshTokenCookieName, out var rawRefresh) &&
            !string.IsNullOrWhiteSpace(rawRefresh))
        {
            var hash = tokenService.HashToken(rawRefresh);
            var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (token is not null && !token.IsRevoked)
            {
                token.RevokedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync();
            }
        }

        AuthCookieHelper.ClearAuthCookies(response);
        return Results.NoContent();
    }
}

