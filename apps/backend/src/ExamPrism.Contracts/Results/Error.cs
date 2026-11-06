namespace Common.Results;

using Common.Validation;

public enum ErrorType
{
    None = 0,
    Failure,
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Unavailable
}

public sealed record Error(
    string Code,
    string Message,
    ErrorType Type = ErrorType.Failure,
    IReadOnlyList<ValidationError>? ValidationErrors = null)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.None);

    public static Error Failure(string code, string message) =>
        new(code, message, ErrorType.Failure);

    public static Error Unauthorized(string code, string message) =>
        new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) =>
        new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) =>
        new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) =>
        new(code, message, ErrorType.Conflict);

    public static Error Unavailable(string code, string message) =>
        new(code, message, ErrorType.Unavailable);

    public static Error Validation(
        IReadOnlyList<ValidationError> errors,
        string message = "One or more validation errors occurred.") =>
        new("VALIDATION_ERROR", message, ErrorType.Validation, errors);

    public static Error Validation(string field, string code, string message) =>
        Validation([new ValidationError(field, code, message)], message);
}

