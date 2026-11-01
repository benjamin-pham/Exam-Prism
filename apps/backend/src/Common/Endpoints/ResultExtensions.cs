namespace Common.Endpoints;

using System.Diagnostics;
using Common.Localization;
using Common.Results;
using Common.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

public static class ResultExtensions
{
    public static IResult ToProblemResult(this Result result, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(httpContext);

        if (result.IsSuccess)
        {
            throw new InvalidOperationException("Cannot convert a successful Result to a ProblemDetails response.");
        }

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        var instance = httpContext.Request.Path.Value ?? string.Empty;

        if (result.Error.Type == ErrorType.Validation)
        {
            var localizer = httpContext.RequestServices.GetService<IStringLocalizer<SharedResources>>();
            var localizedTitle = localizer?["ValidationErrorOccurred"].Value ?? "One or more validation errors occurred.";

            var errors = result.Error.ValidationErrors is { Count: > 0 }
                ? result.Error.ValidationErrors
                : [new ValidationError(string.Empty, result.Error.Code, result.Error.Message)];

            var validationProblem = new ValidationProblemResponse
            {
                Status = StatusCodes.Status400BadRequest,
                Title = localizedTitle,
                Instance = instance,
                TraceId = traceId,
                Errors = errors
            };

            return Results.Problem(validationProblem);
        }

        var (statusCode, title, rfcType) = result.Error.Type switch
        {
            ErrorType.Unauthorized => (StatusCodes.Status401Unauthorized, "Unauthorized", "https://tools.ietf.org/html/rfc7235#section-3.1"),
            ErrorType.Forbidden => (StatusCodes.Status403Forbidden, "Forbidden", "https://tools.ietf.org/html/rfc7231#section-6.5.3"),
            ErrorType.NotFound => (StatusCodes.Status404NotFound, "Resource Not Found", "https://tools.ietf.org/html/rfc7231#section-6.5.4"),
            ErrorType.Conflict => (StatusCodes.Status409Conflict, "Conflict", "https://tools.ietf.org/html/rfc7231#section-6.5.8"),
            ErrorType.Unavailable => (StatusCodes.Status503ServiceUnavailable, "Service Unavailable", "https://tools.ietf.org/html/rfc7231#section-6.6.4"),
            _ => (StatusCodes.Status400BadRequest, "Domain Error", "https://tools.ietf.org/html/rfc7231#section-6.5.1")
        };

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = result.Error.Message,
            Instance = instance,
            Type = rfcType
        };

        problemDetails.Extensions["traceId"] = traceId;
        if (!string.IsNullOrWhiteSpace(result.Error.Code))
        {
            problemDetails.Extensions["errorCode"] = result.Error.Code;
        }

        return Results.Problem(problemDetails);
    }
}

