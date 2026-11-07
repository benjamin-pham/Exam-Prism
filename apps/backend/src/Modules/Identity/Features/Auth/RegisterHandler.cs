namespace ExamPrism.Features.Auth;

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Common.Handlers;
using Common.Results;
using Common.Security;
using Common.Validation;
using ExamPrism.Domain.Identity;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

public record RegisterCommand(
    [Required, EmailAddress] string Email,
    [Required, MinLength(6)] string Password);

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(x => x.Email)
            .NotEmpty().WithErrorCode("REQUIRED")
            .EmailAddress().WithErrorCode("INVALID_EMAIL");

        RuleFor(x => x.Password)
            .NotEmpty().WithErrorCode("REQUIRED")
            .MinimumLength(6).WithErrorCode("PASSWORD_TOO_SHORT");
    }
}

public record RegisterResult(
    Guid Id,
    string Email,
    [property: JsonIgnore] string AccessToken = "",
    [property: JsonIgnore] string RefreshToken = "");

public sealed class RegisterHandler(
    UserManager<ApplicationUser> userManager,
    AppDbContext db,
    ITokenService tokenService,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : IHandler<RegisterCommand, RegisterResult>
{
    public async Task<Result<RegisterResult>> Handle(RegisterCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email) || string.IsNullOrWhiteSpace(command.Password))
        {
            return Error.Validation("Request", "REQUIRED", "Email and password are required.");
        }

        var existing = await userManager.FindByEmailAsync(command.Email);
        if (existing is not null)
        {
            return Error.Validation(nameof(command.Email), "DUPLICATE_EMAIL", "Email is already registered.");
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = command.Email,
            UserName = command.Email
        };

        var identityResult = await userManager.CreateAsync(user, command.Password);
        if (!identityResult.Succeeded)
        {
            var failures = identityResult.Errors
                .Select(e => new ValidationError(e.Code, e.Code, e.Description))
                .ToList();
            return Error.Validation(failures);
        }

        var accessToken = tokenService.GenerateAccessToken(user.Id, user.Email);
        var (rawRefresh, refreshHash) = tokenService.GenerateRefreshToken();

        if (db is not null)
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
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

