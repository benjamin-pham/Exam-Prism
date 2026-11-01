namespace ExamPrism.Features.Auth.Login;

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FluentValidation;

public record LoginCommand(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public class LoginCommandValidator : AbstractValidator<LoginCommand>
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
