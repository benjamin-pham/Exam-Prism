namespace ExamPrism.Tests.Infrastructure;

using System.Diagnostics;
using System.Text.Json;
using Common.Exceptions;
using Common.Localization;
using Common.Validation;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

public class GlobalExceptionHandlerUnitTests
{
    [Theory]
    [InlineData("vi-VN", "Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau.")]
    [InlineData("en-US", "An unexpected error occurred. Please try again later.")]
    public async Task TryHandleAsync_InNonDevelopment_MasksExceptionDetailAndUsesLocalizedMessage(
        string cultureName,
        string expectedMessage)
    {
        var culture = new System.Globalization.CultureInfo(cultureName);
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;

        var logger = Substitute.For<ILogger<GlobalExceptionHandler>>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        var localizer = Substitute.For<IStringLocalizer<SharedResources>>();
        localizer["InternalServerError"].Returns(new LocalizedString("InternalServerError", expectedMessage));

        var handler = new GlobalExceptionHandler(
            logger,
            env,
            localizer,
            problemDetailsService: null);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        var handled = await handler.TryHandleAsync(
            httpContext,
            new InvalidOperationException("Sensitive database credentials or SQL query"),
            CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(httpContext.Response.Body);

        body.GetProperty("status").GetInt32().Should().Be(500);
        body.GetProperty("title").GetString().Should().Be("Internal Server Error");
        body.GetProperty("detail").GetString().Should().Be(expectedMessage);
        body.GetProperty("detail").GetString().Should().NotContain("Sensitive database credentials");
    }

    [Fact]
    public async Task TryHandleAsync_LogsStructuredExceptionWithTraceIdAndPath()
    {
        var logger = Substitute.For<ILogger<GlobalExceptionHandler>>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);

        var localizer = Substitute.For<IStringLocalizer<SharedResources>>();
        localizer["InternalServerError"].Returns(new LocalizedString("InternalServerError", "An unexpected error occurred."));

        var handler = new GlobalExceptionHandler(
            logger,
            env,
            localizer,
            problemDetailsService: null);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/api/test/fail";
        httpContext.TraceIdentifier = "trace-test-123";
        httpContext.Response.Body = new MemoryStream();

        var exception = new InvalidOperationException("Test crash");
        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();

        logger.ReceivedCalls().Should().Contain(call =>
            call.GetMethodInfo().Name == "Log" &&
            call.GetArguments().Any(arg => arg != null && arg.ToString()!.Contains("/api/test/fail") && arg.ToString()!.Contains("trace-test-123")));
    }

    [Fact]
    public async Task TryHandleAsync_DomainException_WithoutErrorCode_DoesNotIncludeErrorCodeExtension()
    {
        var logger = Substitute.For<ILogger<GlobalExceptionHandler>>();
        var env = Substitute.For<IHostEnvironment>();
        var localizer = Substitute.For<IStringLocalizer<SharedResources>>();

        var handler = new GlobalExceptionHandler(
            logger,
            env,
            localizer,
            problemDetailsService: null);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        var exception = new DomainException("Simple domain business error");
        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(httpContext.Response.Body);

        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be("Domain Error");
        body.GetProperty("detail").GetString().Should().Be("Simple domain business error");
        body.GetProperty("type").GetString().Should().Be("https://tools.ietf.org/html/rfc7231#section-6.5.1");
        body.TryGetProperty("errorCode", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryHandleAsync_KeyNotFoundException_InNonDevelopment_MasksExceptionDetail()
    {
        var logger = Substitute.For<ILogger<GlobalExceptionHandler>>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        var localizer = Substitute.For<IStringLocalizer<SharedResources>>();

        var handler = new GlobalExceptionHandler(
            logger,
            env,
            localizer,
            problemDetailsService: null);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        var exception = new KeyNotFoundException("Internal sensitive cache key user:db_hash_999 not found.");
        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(httpContext.Response.Body);

        body.GetProperty("status").GetInt32().Should().Be(404);
        body.GetProperty("title").GetString().Should().Be("Resource Not Found");
        body.GetProperty("detail").GetString().Should().Be("The requested resource was not found.");
        body.GetProperty("detail").GetString().Should().NotContain("user:db_hash_999");
    }

    [Fact]
    public async Task TryHandleAsync_KeyNotFoundException_InDevelopment_ExposesExceptionDetail()
    {
        var logger = Substitute.For<ILogger<GlobalExceptionHandler>>();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);

        var localizer = Substitute.For<IStringLocalizer<SharedResources>>();

        var handler = new GlobalExceptionHandler(
            logger,
            env,
            localizer,
            problemDetailsService: null);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        var exception = new KeyNotFoundException("Internal cache key user:123 not found.");
        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(httpContext.Response.Body);

        body.GetProperty("status").GetInt32().Should().Be(404);
        body.GetProperty("title").GetString().Should().Be("Resource Not Found");
        body.GetProperty("detail").GetString().Should().Be("Internal cache key user:123 not found.");
    }

    [Theory]
    [InlineData(400, "https://tools.ietf.org/html/rfc7231#section-6.5.1")]
    [InlineData(401, "https://tools.ietf.org/html/rfc7235#section-3.1")]
    [InlineData(403, "https://tools.ietf.org/html/rfc7231#section-6.5.3")]
    [InlineData(404, "https://tools.ietf.org/html/rfc7231#section-6.5.4")]
    [InlineData(409, "https://tools.ietf.org/html/rfc7231#section-6.5.8")]
    [InlineData(422, "https://tools.ietf.org/html/rfc4918#section-11.2")]
    public async Task TryHandleAsync_DomainException_SetsCorrectRfcTypeBasedOnStatusCode(int statusCode, string expectedType)
    {
        var logger = Substitute.For<ILogger<GlobalExceptionHandler>>();
        var env = Substitute.For<IHostEnvironment>();
        var localizer = Substitute.For<IStringLocalizer<SharedResources>>();

        var handler = new GlobalExceptionHandler(
            logger,
            env,
            localizer,
            problemDetailsService: null);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        var exception = new DomainException("Domain validation failure", "DOMAIN_ERR", statusCode);
        var handled = await handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        handled.Should().BeTrue();
        httpContext.Response.StatusCode.Should().Be(statusCode);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await JsonSerializer.DeserializeAsync<JsonElement>(httpContext.Response.Body);

        body.GetProperty("type").GetString().Should().Be(expectedType);
    }

    [Fact]
    public void ValidationProblemResponse_Serialization_DoesNotProduceDuplicateKeys()
    {
        var response = new ValidationProblemResponse
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failure",
            Instance = "/api/test",
            TraceId = "trace-456",
            Errors =
            [
                new ValidationError("Email", "INVALID", "Email is invalid"),
                new ValidationError("Age", "RANGE", "Age must be >= 18")
            ]
        };

        var json = JsonSerializer.Serialize(response);

        // Verify JSON string has exactly one "traceId" and one "errors" key
        var traceIdOccurrences = System.Text.RegularExpressions.Regex.Matches(json, @"""traceId""\s*:").Count;
        var errorsOccurrences = System.Text.RegularExpressions.Regex.Matches(json, @"""errors""\s*:").Count;

        traceIdOccurrences.Should().Be(1, "JSON must not serialize traceId twice");
        errorsOccurrences.Should().Be(1, "JSON must not serialize errors twice");

        // Verify deserialization preserves values
        var parsed = JsonSerializer.Deserialize<ValidationProblemResponse>(json);
        parsed.Should().NotBeNull();
        parsed!.TraceId.Should().Be("trace-456");
        parsed.Errors.Should().HaveCount(2);
        parsed.Errors[0].Field.Should().Be("Email");
        parsed.Errors[1].Field.Should().Be("Age");
    }
}
