namespace ExamPrism.Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using global::Tests.Infrastructure;
using Xunit;

public class PipelineIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PipelineIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Pipeline_DefaultCulture_ReturnsVietnameseValidationErrors()
    {
        var response = await _client.PostAsJsonAsync("/api/test/validation", new { Name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be("Một hoặc nhiều lỗi xác thực dữ liệu đã xảy ra.");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("instance").GetString().Should().Be("/api/test/validation");

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().ContainSingle(e =>
            e.GetProperty("field").GetString() == "Name" &&
            e.GetProperty("code").GetString() == "NAME_REQUIRED" &&
            e.GetProperty("message").GetString() == "Tên không được để trống.");
    }

    [Fact]
    public async Task Pipeline_VietnameseCulture_ReturnsVietnameseValidationErrors()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/validation")
        {
            Content = JsonContent.Create(new { Name = "" })
        };
        request.Headers.AcceptLanguage.ParseAdd("vi-VN");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be("Một hoặc nhiều lỗi xác thực dữ liệu đã xảy ra.");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().ContainSingle(e =>
            e.GetProperty("field").GetString() == "Name" &&
            e.GetProperty("code").GetString() == "NAME_REQUIRED" &&
            e.GetProperty("message").GetString() == "Tên không được để trống.");
    }

    [Fact]
    public async Task Pipeline_EnglishCulture_ReturnsEnglishValidationErrors()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/validation")
        {
            Content = JsonContent.Create(new { Name = "" })
        };
        request.Headers.AcceptLanguage.ParseAdd("en-US");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be("One or more validation errors occurred.");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().ContainSingle(e =>
            e.GetProperty("field").GetString() == "Name" &&
            e.GetProperty("code").GetString() == "NAME_REQUIRED" &&
            e.GetProperty("message").GetString() == "Name is required.");
    }

    [Fact]
    public async Task Pipeline_ValidRequest_PassesValidation_ExecutesHandlerSuccessfully()
    {
        var response = await _client.PostAsJsonAsync("/api/test/validation", new { Name = "Valid Pipeline Name" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("Valid");
        body.GetProperty("name").GetString().Should().Be("Valid Pipeline Name");
    }

    [Fact]
    public async Task Pipeline_SkipValidationAttribute_BypassesValidator()
    {
        var response = await _client.PostAsJsonAsync("/api/test/validation/skip", new { Name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("Skipped");
        body.GetProperty("name").GetString().Should().Be("");
    }

    [Fact]
    public async Task Pipeline_UnvalidatedEndpoint_ExecutesDirectly()
    {
        var response = await _client.PostAsJsonAsync("/api/test/validation/unvalidated", new { Info = "Integration Test Payload" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("message").GetString().Should().Be("Unvalidated");
        body.GetProperty("info").GetString().Should().Be("Integration Test Payload");
    }

    [Fact]
    public async Task Pipeline_BadHttpRequest_HandledByGlobalExceptionHandler_Returns400ProblemDetails()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/validation")
        {
            Content = new StringContent("{ invalid-json }", Encoding.UTF8, "application/json")
        };

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be("Bad Request");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("instance").GetString().Should().Be("/api/test/validation");
    }

    [Fact]
    public async Task Pipeline_UnhandledException_CaughtByGlobalExceptionHandler_Returns500ProblemDetails()
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
    public async Task Pipeline_DomainException_WithCustomStatusAndErrorCode_ReturnsExpectedProblemDetails()
    {
        var response = await _client.GetAsync("/api/test/exception/domain");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(409);
        body.GetProperty("title").GetString().Should().Be("Domain Error");
        body.GetProperty("detail").GetString().Should().Be("Operation cannot be performed due to business conflict.");
        body.GetProperty("errorCode").GetString().Should().Be("BUSINESS_CONFLICT");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("instance").GetString().Should().Be("/api/test/exception/domain");
    }

    [Fact]
    public async Task Pipeline_DomainException_DefaultStatus_Returns400BadRequest()
    {
        var response = await _client.GetAsync("/api/test/exception/domain-default");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be("Domain Error");
        body.GetProperty("detail").GetString().Should().Be("Invalid state transition.");
        body.GetProperty("errorCode").GetString().Should().Be("INVALID_STATE");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Pipeline_NotFoundException_Returns404ProblemDetails()
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
    public async Task Pipeline_FullLifecycle_LocalizationValidationAndExceptionHandling_CoexistCorrectly()
    {
        // Step 1: Request Localization + Validation Filter failure (en-US)
        using var invalidEnRequest = new HttpRequestMessage(HttpMethod.Post, "/api/test/validation")
        {
            Content = JsonContent.Create(new { Name = "" })
        };
        invalidEnRequest.Headers.AcceptLanguage.ParseAdd("en-US");
        var invalidEnResponse = await _client.SendAsync(invalidEnRequest);
        invalidEnResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var invalidEnBody = await invalidEnResponse.Content.ReadFromJsonAsync<JsonElement>();
        invalidEnBody.GetProperty("title").GetString().Should().Be("One or more validation errors occurred.");

        // Step 2: Request Localization + Validation Filter failure (vi-VN default)
        var invalidViResponse = await _client.PostAsJsonAsync("/api/test/validation", new { Name = "" });
        invalidViResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var invalidViBody = await invalidViResponse.Content.ReadFromJsonAsync<JsonElement>();
        invalidViBody.GetProperty("title").GetString().Should().Be("Một hoặc nhiều lỗi xác thực dữ liệu đã xảy ra.");

        // Step 3: Valid Request traverses validation filter to endpoint handler
        var validResponse = await _client.PostAsJsonAsync("/api/test/validation", new { Name = "Success User" });
        validResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var validBody = await validResponse.Content.ReadFromJsonAsync<JsonElement>();
        validBody.GetProperty("message").GetString().Should().Be("Valid");
        validBody.GetProperty("name").GetString().Should().Be("Success User");

        // Step 4: Endpoint throwing DomainException caught by GlobalExceptionHandler
        var domainResponse = await _client.GetAsync("/api/test/exception/domain");
        domainResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var domainBody = await domainResponse.Content.ReadFromJsonAsync<JsonElement>();
        domainBody.GetProperty("errorCode").GetString().Should().Be("BUSINESS_CONFLICT");

        // Step 5: Endpoint throwing unhandled exception caught by GlobalExceptionHandler
        var unhandledResponse = await _client.GetAsync("/api/test/exception/unhandled");
        unhandledResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var unhandledBody = await unhandledResponse.Content.ReadFromJsonAsync<JsonElement>();
        unhandledBody.GetProperty("title").GetString().Should().Be("Internal Server Error");
        unhandledBody.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }
}
