namespace ExamPrism.Features.Test;

using Common.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Wolverine.Http;

public static class TestExceptionHandlers
{
    [WolverineGet("/api/test/exception/unhandled")]
    public static void GetUnhandled()
    {
        throw new InvalidOperationException("Something unexpected failed.");
    }

    [WolverineGet("/api/test/exception/not-found")]
    public static void GetNotFound()
    {
        throw new NotFoundException("User with id 123 not found.");
    }

    [WolverineGet("/api/test/exception/key-not-found")]
    public static void GetKeyNotFound()
    {
        throw new KeyNotFoundException("The specified key was not found.");
    }

    [WolverineGet("/api/test/exception/domain")]
    public static void GetDomain()
    {
        throw new DomainException("Operation cannot be performed due to business conflict.", "BUSINESS_CONFLICT", StatusCodes.Status409Conflict);
    }

    [WolverineGet("/api/test/exception/domain-default")]
    public static void GetDomainDefault()
    {
        throw new DomainException("Invalid state transition.", "INVALID_STATE");
    }

    [WolverineGet("/api/test/exception/validation")]
    public static void GetValidation()
    {
        var errors = new[]
        {
            new ValidationFailure("Email", "Email is invalid")
            {
                ErrorCode = "INVALID_EMAIL"
            }
        };
        throw new ValidationException(errors);
    }

    [WolverineGet("/api/test/exception/bad-request")]
    public static void GetBadRequest()
    {
        throw new BadHttpRequestException("Invalid request body.", StatusCodes.Status400BadRequest);
    }

    [WolverineGet("/api/test/exception/unauthorized")]
    public static void GetUnauthorized()
    {
        throw new UnauthorizedAccessException("Access denied.");
    }
}
