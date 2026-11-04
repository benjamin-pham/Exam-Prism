namespace ExamPrism.Features.Platform.Auth;

using System.Text.Json.Serialization;
using Common.Handlers;
using Common.Results;
using Common.Security;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public record PlatformRefreshTokenCommand(string RefreshToken);

public record PlatformRefreshTokenResult(
    [property: JsonIgnore] string AccessToken = "",
    [property: JsonIgnore] string RefreshToken = "");

public sealed class PlatformRefreshTokenHandler(
    CatalogDbContext db,
    UserManager<PlatformUser> userManager,
    ITokenService tokenService,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : IHandler<PlatformRefreshTokenCommand, PlatformRefreshTokenResult>
{
    public async Task<Result<PlatformRefreshTokenResult>> Handle(PlatformRefreshTokenCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken) || db is null)
        {
            return Error.Unauthorized("INVALID_REFRESH_TOKEN", "Invalid or expired refresh token.");
        }

        var hash = tokenService.HashToken(command.RefreshToken);
        var token = await db.PlatformRefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null || token.User is null)
        {
            return Error.Unauthorized("INVALID_REFRESH_TOKEN", "Invalid or expired refresh token.");
        }

        var now = timeProvider.GetUtcNow();

        // Compromised reuse detection
        if (token.IsRevoked || token.ReplacedByTokenHash != null)
        {
            await db.PlatformRefreshTokens
                .Where(t => t.UserId == token.UserId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

            return Error.Unauthorized("COMPROMISED_REFRESH_TOKEN", "Refresh token has been revoked.");
        }

        // Expired check
        if (token.IsExpired(timeProvider))
        {
            return Error.Unauthorized("INVALID_REFRESH_TOKEN", "Invalid or expired refresh token.");
        }

        // Rotate
        var (newRaw, newHash) = tokenService.GenerateRefreshToken();
        var roles = await userManager.GetRolesAsync(token.User);
        var newAccessToken = tokenService.GeneratePlatformAccessToken(token.User.Id, token.User.Email, roles);

        token.RevokedAt = now;
        token.ReplacedByTokenHash = newHash;

        var newToken = new PlatformRefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = token.UserId,
            TokenHash = newHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenExpirationDays)
        };

        db.PlatformRefreshTokens.Add(newToken);
        await db.SaveChangesAsync(ct);

        return new PlatformRefreshTokenResult(newAccessToken, newRaw);
    }
}
