namespace ExamPrism.Tests.Features;

using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class ScalarApiDocsTests
{
    [Fact]
    public async Task GetScalarApiReference_InDevelopment_ReturnsOkAndHtml()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
        });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/scalar/v1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("<title>ExamPrism API</title>");
        content.Should().Contain("scalar");
    }

    [Fact]
    public async Task GetOpenApiDocument_ContainsBearerSecurityScheme()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
        });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(json);
        node.Should().NotBeNull();

        var bearerScheme = node!["components"]?["securitySchemes"]?["Bearer"];
        bearerScheme.Should().NotBeNull();
        bearerScheme!["type"]?.ToString().Should().Be("http");
        bearerScheme!["scheme"]?.ToString().Should().Be("bearer");
        bearerScheme!["bearerFormat"]?.ToString().Should().Be("JWT");
    }

    [Fact]
    public async Task GetScalarApiReference_InProduction_ReturnsNotFound()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
        });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/scalar/v1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

