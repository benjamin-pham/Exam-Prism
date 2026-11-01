namespace ExamPrism.Tests.Infrastructure;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Exceptions;
using Common.Localization;
using Common.Validation;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using NSubstitute;
using global::Tests.Infrastructure;
using Xunit;

public class GlobalExceptionHandlerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public GlobalExceptionHandlerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Get_UnhandledException_Returns500ProblemDetails_WithTraceId()
    {
        var response = await _client.GetAsync("/api/test/exception/unhandled");

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(500);
        body.GetProperty("title").GetString().Should().Be("Internal Server Error");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("instance").GetString().Should().Be("/api/test/exception/unhandled");
    }

    [Fact]
    public async Task Get_NotFoundException_Returns404ProblemDetails()
    {
        var response = await _client.GetAsync("/api/test/exception/not-found");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(404);
        body.GetProperty("title").GetString().Should().Be("Resource Not Found");
        body.GetProperty("detail").GetString().Should().Be("User with id 123 not found.");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Get_KeyNotFoundException_Returns404ProblemDetails()
    {
        var response = await _client.GetAsync("/api/test/exception/key-not-found");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(404);
        body.GetProperty("title").GetString().Should().Be("Resource Not Found");
    }

    [Fact]
    public async Task Get_FluentValidationException_Returns400WithStructuredValidationErrors()
    {
        var response = await _client.GetAsync("/api/test/exception/validation");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().Contain(e =>
            e.GetProperty("field").GetString() == "Email" &&
            e.GetProperty("code").GetString() == "INVALID_EMAIL" &&
            e.GetProperty("message").GetString() == "Email is invalid");
    }

    [Fact]
    public async Task Get_BadHttpRequestException_Returns400BadRequest()
    {
        var response = await _client.GetAsync("/api/test/exception/bad-request");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be("Bad Request");
    }

    [Fact]
    public async Task Get_UnauthorizedAccessException_Returns401Unauthorized()
    {
        var response = await _client.GetAsync("/api/test/exception/unauthorized");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(401);
        body.GetProperty("title").GetString().Should().Be("Unauthorized");
    }

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
}
