namespace ExamPrism.Features.Platform.Auth;

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Common.Handlers;
using Common.Results;
using Common.Security;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

public record PlatformLoginCommand(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public sealed class PlatformLoginCommandValidator : AbstractValidator<PlatformLoginCommand>
{
    public PlatformLoginCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithErrorCode("REQUIRED")
            .EmailAddress().WithErrorCode("INVALID_EMAIL");

        RuleFor(x => x.Password)
            .NotEmpty().WithErrorCode("REQUIRED");
    }
}

public record PlatformLoginResult(
    Guid Id,
    string Email,
    IReadOnlyList<string> Roles,
    [property: JsonIgnore] string AccessToken = "",
    [property: JsonIgnore] string RefreshToken = "");

public sealed class PlatformLoginHandler(
    UserManager<PlatformUser> userManager,
    CatalogDbContext db,
    ITokenService tokenService,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : IHandler<PlatformLoginCommand, PlatformLoginResult>
{
    public async Task<Result<PlatformLoginResult>> Handle(PlatformLoginCommand command, CancellationToken ct = default)
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

        var roles = await userManager.GetRolesAsync(user);
        var accessToken = tokenService.GeneratePlatformAccessToken(user.Id, user.Email, roles);
        var (rawRefresh, refreshHash) = tokenService.GenerateRefreshToken();

        if (db is not null)
        {
            var now = timeProvider.GetUtcNow();
            var refreshToken = new PlatformRefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                TokenHash = refreshHash,
                CreatedAt = now,
                ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenExpirationDays)
            };

            db.PlatformRefreshTokens.Add(refreshToken);
            await db.SaveChangesAsync(ct);
        }

        return new PlatformLoginResult(user.Id, user.Email!, roles.ToList().AsReadOnly(), accessToken, rawRefresh);
    }
}
