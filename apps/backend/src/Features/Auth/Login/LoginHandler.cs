namespace ExamPrism.Features.Auth.Login;

using Common.Handlers;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

public sealed class LoginHandler : IHandler
{
    public static async Task<LoginResult> Handle(
        LoginCommand command,
        UserManager<ApplicationUser> userManager,
        AppDbContext? db,
        ITokenService tokenService,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email) || string.IsNullOrWhiteSpace(command.Password))
        {
            throw new UnauthorizedAccessException("Email and password are required.");
        }

        var user = await userManager.FindByEmailAsync(command.Email);
        if (user is null || !await userManager.CheckPasswordAsync(user, command.Password))
        {
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        var accessToken = tokenService.GenerateAccessToken(user);
        var (rawRefresh, refreshHash) = tokenService.GenerateRefreshToken();

        if (db is not null)
        {
            var now = timeProvider.GetUtcNow();
            var refreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = refreshHash,
                CreatedAt = now,
                ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenExpirationDays)
            };

            db.RefreshTokens.Add(refreshToken);
            await db.SaveChangesAsync(ct);
        }

        return new LoginResult(user.Id, user.Email!, accessToken, rawRefresh);
    }
}
