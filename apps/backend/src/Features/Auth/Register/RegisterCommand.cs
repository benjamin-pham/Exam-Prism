namespace ExamPrism.Features.Auth.Register;

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FluentValidation;

public record RegisterCommand(
    [Required, EmailAddress] string Email,
    [Required, MinLength(6)] string Password);

public class RegisterCommandValidator : AbstractValidator<RegisterCommand>
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
