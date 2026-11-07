namespace ExamPrism.Endpoints.Test;

using Common.Endpoints;
using Common.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public sealed class TestExceptionEndpoints : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/test/exception");

        group.MapGet("/unhandled", () =>
        {
            throw new InvalidOperationException("Something unexpected failed.");
        });

        group.MapGet("/not-found", () =>
        {
            throw new NotFoundException("User with id 123 not found.");
        });

        group.MapGet("/key-not-found", () =>
        {
            throw new KeyNotFoundException("The specified key was not found.");
        });

        group.MapGet("/domain", () =>
        {
            throw new DomainException("Operation cannot be performed due to business conflict.", "BUSINESS_CONFLICT", StatusCodes.Status409Conflict);
        });

        group.MapGet("/domain-default", () =>
        {
            throw new DomainException("Invalid state transition.", "INVALID_STATE");
        });

        group.MapGet("/validation", () =>
        {
            var errors = new[]
            {
                new ValidationFailure("Email", "Email is invalid")
                {
                    ErrorCode = "INVALID_EMAIL"
                }
            };
            throw new ValidationException(errors);
        });

        group.MapGet("/bad-request", () =>
        {
            throw new BadHttpRequestException("Invalid request body.", StatusCodes.Status400BadRequest);
        });

        group.MapGet("/unauthorized", () =>
        {
            throw new UnauthorizedAccessException("Access denied.");
        });
    }
}

