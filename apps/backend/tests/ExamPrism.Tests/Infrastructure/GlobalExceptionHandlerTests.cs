namespace ExamPrism.Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
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
    public async Task Get_DomainException_WithCustomStatusAndErrorCode_ReturnsExpectedProblemDetails()
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
        body.GetProperty("type").GetString().Should().Be("https://tools.ietf.org/html/rfc7231#section-6.5.8");
    }

    [Fact]
    public async Task Get_DomainException_WithDefaultStatus_Returns400BadRequestAndErrorCode()
    {
        var response = await _client.GetAsync("/api/test/exception/domain-default");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);
        body.GetProperty("title").GetString().Should().Be("Domain Error");
        body.GetProperty("detail").GetString().Should().Be("Invalid state transition.");
        body.GetProperty("errorCode").GetString().Should().Be("INVALID_STATE");
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("type").GetString().Should().Be("https://tools.ietf.org/html/rfc7231#section-6.5.1");
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
}
