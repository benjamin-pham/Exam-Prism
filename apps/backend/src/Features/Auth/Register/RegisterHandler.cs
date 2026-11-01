namespace ExamPrism.Features.Auth.Register;

using Common.Handlers;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

public sealed class RegisterHandler : IHandler
{
    public static async Task<RegisterResult> Handle(
        RegisterCommand command,
        UserManager<ApplicationUser> userManager,
        AppDbContext? db,
        ITokenService tokenService,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email) || string.IsNullOrWhiteSpace(command.Password))
        {
            throw new ValidationException(new[]
            {
                new ValidationFailure("Request", "Email and password are required.")
                {
                    ErrorCode = "REQUIRED"
                }
            });
        }

        var existing = await userManager.FindByEmailAsync(command.Email);
        if (existing is not null)
        {
            throw new ValidationException(new[]
            {
                new ValidationFailure(nameof(command.Email), "Email is already registered.")
                {
                    ErrorCode = "DUPLICATE_EMAIL"
                }
            });
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = command.Email,
            UserName = command.Email
        };

        var result = await userManager.CreateAsync(user, command.Password);
        if (!result.Succeeded)
        {
            var failures = result.Errors
                .Select(e => new ValidationFailure(e.Code, e.Description) { ErrorCode = e.Code });
            throw new ValidationException(failures);
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

        return new RegisterResult(user.Id, user.Email!, accessToken, rawRefresh);
    }
}

