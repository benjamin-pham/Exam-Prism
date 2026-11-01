namespace ExamPrism.Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Validation;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using global::Tests.Infrastructure;
using Xunit;

public class ValidationFilterTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ValidationFilterTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
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

    [Theory]
    [InlineData("vi-VN", "Dữ liệu yêu cầu không được để trống.")]
    [InlineData("en-US", "Request body cannot be null.")]
    public async Task ValidationFilter_WhenArgumentIsNullAndNotOptional_Returns400WithLocalizedNullMessage(string cultureName, string expectedMessage)
    {
        var culture = new System.Globalization.CultureInfo(cultureName);
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;

        using var scope = _factory.Services.CreateScope();
        var httpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };

        var endpointMethod = ((Delegate)((ExamPrism.Endpoints.Test.TestValidationRequest req) => Microsoft.AspNetCore.Http.Results.Ok())).Method;
        var factoryContext = new Microsoft.AspNetCore.Http.EndpointFilterFactoryContext
        {
            MethodInfo = endpointMethod,
            ApplicationServices = _factory.Services
        };

        var filter = ValidationFilterFactory.Create(factoryContext, _ => ValueTask.FromResult<object?>(Microsoft.AspNetCore.Http.Results.Ok()));
        var invocationContext = Microsoft.AspNetCore.Http.EndpointFilterInvocationContext.Create(httpContext, (object?)null);

        var result = await filter(invocationContext);

        var problemResult = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>().Subject;
        problemResult.StatusCode.Should().Be(400);

        var problemResponse = problemResult.ProblemDetails.Should().BeOfType<ValidationProblemResponse>().Subject;
        problemResponse.Errors.Should().Contain(e =>
            e.Code == "REQUIRED" &&
            e.Message == expectedMessage);
    }
}
