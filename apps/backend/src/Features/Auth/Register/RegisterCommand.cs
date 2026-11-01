namespace ExamPrism.Features.Auth.Register;

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

public record RegisterCommand(
    [Required, EmailAddress] string Email,
    [Required, MinLength(6)] string Password);

public record RegisterResult(
    Guid Id,
    string Email,
    [property: JsonIgnore] string AccessToken = "",
    [property: JsonIgnore] string RefreshToken = "");
