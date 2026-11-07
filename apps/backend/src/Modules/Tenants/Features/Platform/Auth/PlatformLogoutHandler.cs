namespace ExamPrism.Features.Platform.Auth;

using Common.Handlers;
using Common.Results;
using Common.Security;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;

public record PlatformLogoutCommand(string? RefreshToken);

public sealed class PlatformLogoutHandler(
    CatalogDbContext db,
    ITokenService tokenService,
    TimeProvider timeProvider) : IHandler<PlatformLogoutCommand>
{
    public async Task<Result> Handle(PlatformLogoutCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.RefreshToken) || db is null)
        {
            return Result.Success();
        }

        var hash = tokenService.HashToken(command.RefreshToken);
        var token = await db.PlatformRefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is not null && !token.IsRevoked)
        {
            token.RevokedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        return Result.Success();
    }
}
