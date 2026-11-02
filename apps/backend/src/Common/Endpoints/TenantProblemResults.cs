namespace Common.Endpoints;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Common.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

public static class TenantProblemResults
{
    public const string TenantNotFound = "TENANT_NOT_FOUND";
    public const string TenantProvisioning = "TENANT_PROVISIONING";
    public const string TenantSuspended = "TENANT_SUSPENDED";
    public const string TenantUnavailable = "TENANT_UNAVAILABLE";
    public const string TenantRequired = "TENANT_REQUIRED";
    public const string PlatformOnly = "PLATFORM_ONLY";
    public const string TenantTokenMismatch = "TENANT_TOKEN_MISMATCH";

    public static ProblemDetails CreateProblemDetails(
        HttpContext httpContext,
        int statusCode,
        string errorCode,
        string resourceKey,
        string defaultDetail)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);

        var localizer = httpContext.RequestServices.GetService<IStringLocalizer<SharedResources>>();
        var detail = localizer?[resourceKey]?.Value ?? defaultDetail;
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = GetTitleForStatusCode(statusCode),
            Type = GetRfcTypeForStatusCode(statusCode),
            Detail = detail,
            Instance = httpContext.Request.Path.Value ?? string.Empty
        };

        problem.Extensions["traceId"] = traceId;
        problem.Extensions["errorCode"] = errorCode;

        return problem;
    }

    public static async Task WriteProblemAsync(
        HttpContext httpContext,
        int statusCode,
        string errorCode,
        string resourceKey,
        string defaultDetail,
        Action<HttpResponse>? configureResponse = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Response.HasStarted)
        {
            return;
        }

        var problem = CreateProblemDetails(httpContext, statusCode, errorCode, resourceKey, defaultDetail);

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/problem+json";
        configureResponse?.Invoke(httpContext.Response);

        await httpContext.Response.WriteAsJsonAsync(
            problem,
            options: (JsonSerializerOptions?)null,
            contentType: "application/problem+json",
            cancellationToken: cancellationToken);
    }

    public static IResult CreateProblemResult(
        HttpContext httpContext,
        int statusCode,
        string errorCode,
        string resourceKey,
        string defaultDetail)
    {
        var problem = CreateProblemDetails(httpContext, statusCode, errorCode, resourceKey, defaultDetail);
        return Results.Problem(problem);
    }

    public static Task WriteTenantNotFoundAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default) =>
        WriteProblemAsync(
            httpContext,
            StatusCodes.Status404NotFound,
            TenantNotFound,
            "TenantNotFound",
            "The requested organization was not found.",
            null,
            cancellationToken);

    public static Task WriteTenantProvisioningAsync(
        HttpContext httpContext,
        int retryAfterSeconds = 5,
        CancellationToken cancellationToken = default) =>
        WriteProblemAsync(
            httpContext,
            StatusCodes.Status503ServiceUnavailable,
            TenantProvisioning,
            "TenantProvisioning",
            "This organization workspace is currently being provisioned. Please try again shortly.",
            resp => resp.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture),
            cancellationToken);

    public static Task WriteTenantSuspendedAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default) =>
        WriteProblemAsync(
            httpContext,
            StatusCodes.Status403Forbidden,
            TenantSuspended,
            "TenantSuspended",
            "This organization workspace has been suspended.",
            null,
            cancellationToken);

    public static Task WriteTenantUnavailableAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default) =>
        WriteProblemAsync(
            httpContext,
            StatusCodes.Status403Forbidden,
            TenantUnavailable,
            "TenantUnavailable",
            "This organization workspace is currently unavailable.",
            null,
            cancellationToken);

    public static Task WriteTenantTokenMismatchAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken = default) =>
        WriteProblemAsync(
            httpContext,
            StatusCodes.Status401Unauthorized,
            TenantTokenMismatch,
            "TenantTokenMismatch",
            "The authentication token does not belong to this organization.",
            null,
            cancellationToken);

    public static IResult TenantRequiredResult(HttpContext httpContext) =>
        CreateProblemResult(
            httpContext,
            StatusCodes.Status400BadRequest,
            TenantRequired,
            "TenantRequired",
            "A valid organization subdomain is required for this endpoint.");

    public static IResult PlatformOnlyResult(HttpContext httpContext) =>
        CreateProblemResult(
            httpContext,
            StatusCodes.Status404NotFound,
            PlatformOnly,
            "PlatformOnly",
            "This endpoint is only available on the platform domain.");

    public static IResult TenantTokenMismatchResult(HttpContext httpContext) =>
        CreateProblemResult(
            httpContext,
            StatusCodes.Status401Unauthorized,
            TenantTokenMismatch,
            "TenantTokenMismatch",
            "The authentication token does not belong to this organization.");

    private static string GetTitleForStatusCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
        _ => "An error occurred"
    };

    private static string GetRfcTypeForStatusCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "https://tools.ietf.org/html/rfc7231#section-6.5.1",
        StatusCodes.Status401Unauthorized => "https://tools.ietf.org/html/rfc7235#section-3.1",
        StatusCodes.Status403Forbidden => "https://tools.ietf.org/html/rfc7231#section-6.5.3",
        StatusCodes.Status404NotFound => "https://tools.ietf.org/html/rfc7231#section-6.5.4",
        StatusCodes.Status503ServiceUnavailable => "https://tools.ietf.org/html/rfc7231#section-6.6.4",
        _ => "https://tools.ietf.org/html/rfc7231#section-6.5.1"
    };
}
