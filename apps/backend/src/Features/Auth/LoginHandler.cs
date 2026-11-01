namespace ExamPrism.Features.Auth;

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Common.Handlers;
using Common.Results;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

public record LoginCommand(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithErrorCode("REQUIRED")
            .EmailAddress().WithErrorCode("INVALID_EMAIL");

        RuleFor(x => x.Password)
            .NotEmpty().WithErrorCode("REQUIRED");
    }
}

public record LoginResult(
    Guid Id,
    string Email,
    [property: JsonIgnore] string AccessToken = "",
    [property: JsonIgnore] string RefreshToken = "");

public sealed class LoginHandler(
    UserManager<ApplicationUser> userManager,
    AppDbContext db,
    ITokenService tokenService,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : IHandler<LoginCommand, LoginResult>
{
    public async Task<Result<LoginResult>> Handle(LoginCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email) || string.IsNullOrWhiteSpace(command.Password))
        {
            return Error.Unauthorized("INVALID_CREDENTIALS", "Invalid email or password.");
        }

        var user = await userManager.FindByEmailAsync(command.Email);
        if (user is null || !await userManager.CheckPasswordAsync(user, command.Password))
        {
            return Error.Unauthorized("INVALID_CREDENTIALS", "Invalid email or password.");
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

