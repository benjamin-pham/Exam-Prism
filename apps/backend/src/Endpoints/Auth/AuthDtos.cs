namespace ExamPrism.Endpoints.Auth;

using System.ComponentModel.DataAnnotations;

public record RegisterRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(6)] string Password);

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public record UserResponse(Guid Id, string Email);
