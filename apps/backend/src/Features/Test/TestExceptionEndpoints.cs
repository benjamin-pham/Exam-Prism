namespace ExamPrism.Features.Test;

using Common.Endpoints;
using Common.Exceptions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public sealed class TestExceptionEndpoints : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        var env = (app as WebApplication)?.Environment
            ?? app.ServiceProvider?.GetService<IHostEnvironment>();

        if (env is null || !env.IsDevelopment())
        {
            return;
        }

        app.MapGet("/api/test/exception/unhandled", () =>
        {
            throw new InvalidOperationException("Something unexpected failed.");
        });

        app.MapGet("/api/test/exception/not-found", () =>
        {
            throw new NotFoundException("User with id 123 not found.");
        });

        app.MapGet("/api/test/exception/key-not-found", () =>
        {
            throw new KeyNotFoundException("The specified key was not found.");
        });

        app.MapGet("/api/test/exception/domain", () =>
        {
            throw new DomainException("Operation cannot be performed due to business conflict.", "BUSINESS_CONFLICT", StatusCodes.Status409Conflict);
        });

        app.MapGet("/api/test/exception/domain-default", () =>
        {
            throw new DomainException("Invalid state transition.", "INVALID_STATE");
        });

        app.MapGet("/api/test/exception/validation", () =>
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

        app.MapGet("/api/test/exception/bad-request", () =>
        {
            throw new BadHttpRequestException("Invalid request body.", StatusCodes.Status400BadRequest);
        });

        app.MapGet("/api/test/exception/unauthorized", () =>
        {
            throw new UnauthorizedAccessException("Access denied.");
        });
    }
}
