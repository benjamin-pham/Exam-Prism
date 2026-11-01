namespace ExamPrism.Features.Auth;

using System.Text.Json.Serialization;
using Common.Handlers;
using Common.Results;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public record RefreshTokenCommand(string RefreshToken);

public record RefreshTokenResult(
    [property: JsonIgnore] string AccessToken = "",
    [property: JsonIgnore] string RefreshToken = "");

public sealed class RefreshTokenHandler(
    AppDbContext db,
    ITokenService tokenService,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : IHandler<RefreshTokenCommand, RefreshTokenResult>
{
    public async Task<Result<RefreshTokenResult>> Handle(RefreshTokenCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken) || db is null)
        {
            return Error.Unauthorized("INVALID_REFRESH_TOKEN", "Invalid or expired refresh token.");
        }

        var hash = tokenService.HashToken(command.RefreshToken);
        var token = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null)
        {
            return Error.Unauthorized("INVALID_REFRESH_TOKEN", "Invalid or expired refresh token.");
        }

        var now = timeProvider.GetUtcNow();

        // Compromised reuse detection
        if (token.IsRevoked || token.ReplacedByTokenHash != null)
        {
            await db.RefreshTokens
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
        await db.SaveChangesAsync(ct);

        return new RefreshTokenResult(newAccessToken, newRaw);
    }
}
