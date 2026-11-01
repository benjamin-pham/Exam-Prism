namespace ExamPrism.Features.Auth.Logout;

using Common.Handlers;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public sealed class LogoutHandler : IHandler
{
    public static async Task Handle(
        LogoutCommand command,
        AppDbContext? db,
        ITokenService tokenService,
        TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken) || db is null)
        {
            return;
        }

        var hash = tokenService.HashToken(command.RefreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is not null && !token.IsRevoked)
        {
            token.RevokedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
    }
}

