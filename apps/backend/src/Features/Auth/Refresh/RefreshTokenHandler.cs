namespace ExamPrism.Features.Auth.Refresh;

using Common.Handlers;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public sealed class RefreshTokenHandler : IHandler
{
    public static async Task<RefreshTokenResult?> Handle(
        RefreshTokenCommand command,
        AppDbContext? db,
        ITokenService tokenService,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken) || db is null)
        {
            return null;
        }

        var hash = tokenService.HashToken(command.RefreshToken);
        var token = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (token is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();

        // Compromised reuse detection
        if (token.IsRevoked || token.ReplacedByTokenHash != null)
        {
            await db.RefreshTokens
                .Where(t => t.UserId == token.UserId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

            return null;
        }

        // Expired check
        if (token.IsExpired(timeProvider))
        {
            return null;
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
