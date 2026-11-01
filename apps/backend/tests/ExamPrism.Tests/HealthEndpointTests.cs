using System.Net;
using System.Net.Http.Json;
using ExamPrism.Features.Health.GetHealth;
using FluentAssertions;
using Tests.Infrastructure;
using Xunit;

namespace Tests;

public class HealthEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/Health")]
    [InlineData("/HEALTH")]
    public async Task GetHealth_HandlesCaseVariations(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync<GetHealthResult>();
        content.Should().NotBeNull();
        content!.Status.Should().Be("Healthy");
    }

    [Fact]
    public async Task GetOpenApi_ReturnsOk_InDevelopment()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().ContainEquivalentOf("openapi");
    }
}
