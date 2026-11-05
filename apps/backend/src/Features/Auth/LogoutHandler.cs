namespace ExamPrism.Features.Auth;

using Common.Handlers;
using Common.Results;
using Common.Security;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

public record LogoutCommand(string? RefreshToken);

public sealed class LogoutHandler(
    AppDbContext db,
    ITokenService tokenService,
    TimeProvider timeProvider) : IHandler<LogoutCommand>
{
    public async Task<Result> Handle(LogoutCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken) || db is null)
        {
            return Result.Success();
        }

        var hash = tokenService.HashToken(command.RefreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is not null && !token.IsRevoked)
        {
            token.RevokedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        return Result.Success();
    }
}

