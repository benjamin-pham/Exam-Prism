namespace Common.Validation;

public record ValidationError(string Field, string Code, string Message);
