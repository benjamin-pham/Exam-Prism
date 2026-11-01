namespace Common.Exceptions;

using System.Diagnostics;
using Common.Localization;
using Common.Validation;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

/// <summary>
/// Global exception handler implementing ASP.NET Core 10 <see cref="IExceptionHandler"/>.
/// Maps domain exceptions, FluentValidation exceptions, and unhandled exceptions
/// to standardized RFC 7807 ProblemDetails responses.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;
    private readonly IStringLocalizer<SharedResources> _localizer;
    private readonly IProblemDetailsService? _problemDetailsService;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
        IHostEnvironment environment,
        IStringLocalizer<SharedResources> localizer,
        IProblemDetailsService? problemDetailsService = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _problemDetailsService = problemDetailsService;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (httpContext.Response.HasStarted)
        {
            _logger.LogWarning("The response has already started, cannot write ProblemDetails for exception at {Path}", httpContext.Request.Path);
            return false;
        }

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        var path = httpContext.Request.Path.Value ?? string.Empty;

        ProblemDetails problemDetails;
        int statusCode;

        switch (exception)
        {
            case ValidationException validationException:
                statusCode = StatusCodes.Status400BadRequest;
                var validationTitle = _localizer["ValidationErrorOccurred"].Value ?? "One or more validation errors occurred.";
                problemDetails = new ValidationProblemResponse
                {
                    Status = statusCode,
                    Title = validationTitle,
                    Instance = path,
                    TraceId = traceId,
                    Errors = validationException.Errors
                        .Select(e => new ValidationError(e.PropertyName, e.ErrorCode, e.ErrorMessage))
                        .ToList()
                };
                _logger.LogWarning(validationException, "Validation exception occurred at {Path} (TraceId: {TraceId})", path, traceId);
                break;

            case BadHttpRequestException badHttpRequestException:
                statusCode = badHttpRequestException.StatusCode;
                problemDetails = new ProblemDetails
                {
                    Status = statusCode,
                    Title = "Bad Request",
                    Detail = badHttpRequestException.Message,
                    Instance = path,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.1"
                };
                problemDetails.Extensions["traceId"] = traceId;
                _logger.LogWarning(badHttpRequestException, "Bad HTTP request at {Path} (TraceId: {TraceId})", path, traceId);
                break;

            case NotFoundException notFoundException:
                statusCode = StatusCodes.Status404NotFound;
                problemDetails = new ProblemDetails
                {
                    Status = statusCode,
                    Title = "Resource Not Found",
                    Detail = notFoundException.Message,
                    Instance = path,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.4"
                };
                problemDetails.Extensions["traceId"] = traceId;
                _logger.LogWarning(notFoundException, "Resource not found at {Path} (TraceId: {TraceId})", path, traceId);
                break;

            case KeyNotFoundException keyNotFoundException:
                statusCode = StatusCodes.Status404NotFound;
                problemDetails = new ProblemDetails
                {
                    Status = statusCode,
                    Title = "Resource Not Found",
                    Detail = keyNotFoundException.Message,
                    Instance = path,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.4"
                };
                problemDetails.Extensions["traceId"] = traceId;
                _logger.LogWarning(keyNotFoundException, "Key not found at {Path} (TraceId: {TraceId})", path, traceId);
                break;

            case UnauthorizedAccessException unauthorizedAccessException:
                statusCode = StatusCodes.Status401Unauthorized;
                problemDetails = new ProblemDetails
                {
                    Status = statusCode,
                    Title = "Unauthorized",
                    Detail = unauthorizedAccessException.Message,
                    Instance = path,
                    Type = "https://tools.ietf.org/html/rfc7235#section-3.1"
                };
                problemDetails.Extensions["traceId"] = traceId;
                _logger.LogWarning(unauthorizedAccessException, "Unauthorized access at {Path} (TraceId: {TraceId})", path, traceId);
                break;

            default:
                statusCode = StatusCodes.Status500InternalServerError;
                var internalServerErrorMessage = _localizer["InternalServerError"].Value ?? "An unexpected error occurred. Please try again later.";
                var detail = _environment.IsDevelopment()
                    ? exception.ToString()
                    : internalServerErrorMessage;

                problemDetails = new ProblemDetails
                {
                    Status = statusCode,
                    Title = "Internal Server Error",
                    Detail = detail,
                    Instance = path,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1"
                };
                problemDetails.Extensions["traceId"] = traceId;
                _logger.LogError(exception, "Unhandled exception occurred at {Path} (TraceId: {TraceId})", path, traceId);
                break;
        }

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";

        if (_problemDetailsService is not null)
        {
            var handled = await _problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problemDetails,
                Exception = exception
            });

            if (handled)
            {
                return true;
            }
        }

        await httpContext.Response.WriteAsJsonAsync(problemDetails, problemDetails.GetType(), cancellationToken: cancellationToken);
        return true;
    }
}
