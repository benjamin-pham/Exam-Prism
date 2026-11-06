namespace Common.Exceptions;

using Microsoft.AspNetCore.Http;

/// <summary>
/// Domain exception thrown when a requested resource or entity is not found.
/// Maps to HTTP 404 Not Found.
/// </summary>
public class NotFoundException : DomainException
{
    public NotFoundException()
        : base("The requested resource was not found.", "NOT_FOUND", StatusCodes.Status404NotFound)
    {
    }

    public NotFoundException(string message)
        : base(message, "NOT_FOUND", StatusCodes.Status404NotFound)
    {
    }

    public NotFoundException(string message, Exception innerException)
        : base(message, innerException, "NOT_FOUND", StatusCodes.Status404NotFound)
    {
    }

    public NotFoundException(string name, object key)
        : base($"Entity \"{name}\" ({key}) was not found.", "NOT_FOUND", StatusCodes.Status404NotFound)
    {
    }
}
