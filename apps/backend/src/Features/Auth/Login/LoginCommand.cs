namespace ExamPrism.Features.Auth.Login;

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

public record LoginCommand(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public record LoginResult(
    Guid Id,
    string Email,
    [property: JsonIgnore] string AccessToken = "",
    [property: JsonIgnore] string RefreshToken = "");
