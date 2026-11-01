namespace Common.Validation;

using System.Diagnostics;
using Common.Localization;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Wolverine.Http.FluentValidation;

public class CustomProblemDetailSource<T> : IProblemDetailSource<T>
{
    private readonly IStringLocalizer<SharedResources> _localizer;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CustomProblemDetailSource(
        IStringLocalizer<SharedResources> localizer,
        IHttpContextAccessor httpContextAccessor)
    {
        _localizer = localizer;
        _httpContextAccessor = httpContextAccessor;
    }

    public ProblemDetails Create(T message, IReadOnlyList<ValidationFailure> failures)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        var localizedTitle = _localizer["ValidationTitle"].Value;
        if (string.IsNullOrWhiteSpace(localizedTitle) || localizedTitle == "ValidationTitle")
        {
            localizedTitle = "Một hoặc nhiều lỗi xác thực dữ liệu đã xảy ra.";
        }

        var traceId = Activity.Current?.Id ?? httpContext?.TraceIdentifier;

        var errors = failures.Select(f => new ValidationError(
            f.PropertyName,
            f.ErrorCode,
            f.ErrorMessage)).ToList();

        var response = new ValidationProblemResponse
        {
            Title = localizedTitle,
            Status = StatusCodes.Status400BadRequest,
            Instance = httpContext?.Request.Path.Value,
            TraceId = traceId,
            Errors = errors
        };

        return response;
    }
}
