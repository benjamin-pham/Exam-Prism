namespace ExamPrism.Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Validation;
using FluentAssertions;
using global::Tests.Infrastructure;
using Xunit;

public class ValidationFilterTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ValidationFilterTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Post_InvalidRequest_Returns400WithErrorsAndErrorCode()
    {
        var response = await _client.PostAsJsonAsync("/api/test/validation", new { Name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().Contain(e =>
            e.GetProperty("field").GetString() == "Name" &&
            e.GetProperty("code").GetString() == "NAME_REQUIRED" &&
            e.GetProperty("message").GetString() == "Tên không được để trống.");
    }

    [Fact]
    public async Task Post_ValidRequest_Returns200()
    {
        var response = await _client.PostAsJsonAsync("/api/test/validation", new { Name = "Valid Name" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_InvalidRequest_WithEnglishCulture_ReturnsLocalizedEnglishMessage()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/test/validation")
        {
            Content = JsonContent.Create(new { Name = "" })
        };
        request.Headers.AcceptLanguage.ParseAdd("en-US");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().Contain(e =>
            e.GetProperty("field").GetString() == "Name" &&
            e.GetProperty("code").GetString() == "NAME_REQUIRED" &&
            e.GetProperty("message").GetString() == "Name is required.");
    }

    [Fact]
    public async Task Post_InvalidRequest_ToSkipValidationEndpoint_Returns200()
    {
        var response = await _client.PostAsJsonAsync("/api/test/validation/skip", new { Name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_UnvalidatedEndpoint_ExecutesTransparently()
    {
        var response = await _client.PostAsJsonAsync("/api/test/validation/unvalidated", new { Info = "Hello World" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_MultiParamEndpoint_ValidatesOnlyParameterWithValidator()
    {
        var response = await _client.PostAsJsonAsync("/api/test/validation/multi-param", new { Name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().Contain(e =>
            e.GetProperty("field").GetString() == "Name" &&
            e.GetProperty("code").GetString() == "NAME_REQUIRED");
    }
}
