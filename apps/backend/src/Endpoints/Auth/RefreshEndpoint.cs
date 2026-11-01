namespace ExamPrism.Endpoints.Auth;

using Common.Endpoints;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class RefreshEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/refresh", HandleAsync)
            .WithTags("Authentication")
            .WithName("Refresh")
            .WithDescription("Rotates refresh token and returns updated auth cookies");
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        HttpResponse response,
        AppDbContext db,
        ITokenService tokenService,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider)
    {
        if (!request.Cookies.TryGetValue(AuthCookieHelper.RefreshTokenCookieName, out var rawRefresh) ||
            string.IsNullOrWhiteSpace(rawRefresh))
        {
            return Results.Unauthorized();
        }

        var hash = tokenService.HashToken(rawRefresh);
        var token = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (token is null)
        {
            AuthCookieHelper.ClearAuthCookies(response);
            return Results.Unauthorized();
        }

        var now = timeProvider.GetUtcNow();

        // Compromised reuse detection
        if (token.IsRevoked || token.ReplacedByTokenHash != null)
        {
            await db.RefreshTokens
                .Where(t => t.UserId == token.UserId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));

            AuthCookieHelper.ClearAuthCookies(response);
            return Results.Unauthorized();
        }

        // Expired check
        if (token.IsExpired(timeProvider))
        {
            AuthCookieHelper.ClearAuthCookies(response);
            return Results.Unauthorized();
        }

        // Rotate
        var (newRaw, newHash) = tokenService.GenerateRefreshToken();
        var newAccessToken = tokenService.GenerateAccessToken(token.User);

        token.RevokedAt = now;
        token.ReplacedByTokenHash = newHash;

        var newToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = token.UserId,
            TokenHash = newHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenExpirationDays)
        };

        db.RefreshTokens.Add(newToken);
        await db.SaveChangesAsync();

        AuthCookieHelper.SetAuthCookies(response, newAccessToken, newRaw, jwtOptions.Value, timeProvider);

        return Results.Ok();
    }
}
