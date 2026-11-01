namespace ExamPrism.Features.Auth;

using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Wolverine.Http;

public static class LogoutHandler
{
    [WolverinePost("/api/auth/logout")]
    public static async Task<IResult> Handle(
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
