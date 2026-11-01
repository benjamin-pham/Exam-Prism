namespace ExamPrism.Tests.Results;

using System.Diagnostics;
using System.Text.Json;
using Common.Endpoints;
using Common.Localization;
using Common.Results;
using Common.Validation;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class ResultExtensionsTests
{
    private static (HttpContext Context, MemoryStream Body) CreateHttpContext(string culture = "vi-VN", string path = "/api/test")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddProblemDetails();
        services.AddLocalization(o => o.ResourcesPath = "Common/Localization/Resources");
        var provider = services.BuildServiceProvider();

        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = provider,
            Response = { Body = body }
        };
        context.Request.Path = path;

        var ci = new System.Globalization.CultureInfo(culture);
        System.Globalization.CultureInfo.CurrentCulture = ci;
        System.Globalization.CultureInfo.CurrentUICulture = ci;

        return (context, body);
    }

    private static async Task<JsonElement> ReadResponseBody(MemoryStream body)
    {
        body.Position = 0;
        using var reader = new StreamReader(body);
        var json = await reader.ReadToEndAsync();
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    [Fact]
    public void ToProblemResult_OnSuccessResult_ThrowsInvalidOperationException()
    {
        var (context, _) = CreateHttpContext();
        var ok = Result.Success();

        var act = () => ok.ToProblemResult(context);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot convert a successful Result to a ProblemDetails response*");
    }

    [Theory]
    [InlineData(ErrorType.Unauthorized, 401, "Unauthorized", "https://tools.ietf.org/html/rfc7235#section-3.1")]
    [InlineData(ErrorType.Forbidden, 403, "Forbidden", "https://tools.ietf.org/html/rfc7231#section-6.5.3")]
    [InlineData(ErrorType.NotFound, 404, "Resource Not Found", "https://tools.ietf.org/html/rfc7231#section-6.5.4")]
    [InlineData(ErrorType.Conflict, 409, "Conflict", "https://tools.ietf.org/html/rfc7231#section-6.5.8")]
    [InlineData(ErrorType.Unavailable, 503, "Service Unavailable", "https://tools.ietf.org/html/rfc7231#section-6.6.4")]
    [InlineData(ErrorType.Failure, 400, "Domain Error", "https://tools.ietf.org/html/rfc7231#section-6.5.1")]
    public async Task ToProblemResult_MapsEachNonValidationErrorType_ToExpectedStatusAndProblemDetails(
        ErrorType errorType,
        int expectedStatus,
        string expectedTitle,
        string expectedType)
    {
        var (context, body) = CreateHttpContext();
        var error = new Error("TEST_CODE", "Test detail message.", errorType);
        var result = Result.Failure(error);

        var httpResult = result.ToProblemResult(context);
        await httpResult.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(expectedStatus);

        var json = await ReadResponseBody(body);
        json.GetProperty("status").GetInt32().Should().Be(expectedStatus);
        json.GetProperty("title").GetString().Should().Be(expectedTitle);
        json.GetProperty("type").GetString().Should().Be(expectedType);
        json.GetProperty("detail").GetString().Should().Be("Test detail message.");
        json.GetProperty("errorCode").GetString().Should().Be("TEST_CODE");
        json.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        json.GetProperty("instance").GetString().Should().Be("/api/test");
    }

    [Theory]
    [InlineData("vi-VN", "Một hoặc nhiều lỗi xác thực dữ liệu đã xảy ra.")]
    [InlineData("en-US", "One or more validation errors occurred.")]
    public async Task ToProblemResult_ValidationWithNullErrors_SynthesizesFallbackError_AndLocalizesTitle(
        string culture,
        string expectedTitle)
    {
        var (context, body) = CreateHttpContext(culture);
        var error = new Error("GENERIC_VAL", "Generic validation failure.", ErrorType.Validation, null);
        var result = Result.Failure(error);

        var httpResult = result.ToProblemResult(context);
        await httpResult.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        var json = await ReadResponseBody(body);
        json.GetProperty("status").GetInt32().Should().Be(400);
        json.GetProperty("title").GetString().Should().Be(expectedTitle);
        json.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();

        var errors = json.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().ContainSingle(e =>
            e.GetProperty("code").GetString() == "GENERIC_VAL" &&
            e.GetProperty("message").GetString() == "Generic validation failure.");
    }
}
