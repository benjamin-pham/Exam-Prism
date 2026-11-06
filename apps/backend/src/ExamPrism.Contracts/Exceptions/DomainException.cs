namespace Common.Exceptions;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Base exception type for all domain-specific business exceptions.
/// </summary>
public class DomainException : Exception
{
    public int StatusCode { get; }
    public string? ErrorCode { get; }

    public DomainException()
        : this("A domain error occurred.")
    {
    }

    public DomainException(string message, string? errorCode = null, int statusCode = StatusCodes.Status400BadRequest)
        : base(message)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode > 0 ? statusCode : StatusCodes.Status400BadRequest;
    }

    public DomainException(string message, Exception innerException, string? errorCode = null, int statusCode = StatusCodes.Status400BadRequest)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        StatusCode = statusCode > 0 ? statusCode : StatusCodes.Status400BadRequest;
    }
}
