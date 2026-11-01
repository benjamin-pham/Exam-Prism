namespace Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using Endpoints;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class DatabaseIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DatabaseIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CanConnectToDatabase_ReturnsTrue()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var canConnect = await dbContext.Database.CanConnectAsync();

        Assert.True(canConnect);
    }

    [Fact]
    public async Task HealthEndpoint_WithDatabaseCheck_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(content);
        Assert.Equal("Healthy", content.Status);
    }

    [Fact]
    public async Task HealthEndpoint_WithUnreachableDatabase_ReturnsServiceUnavailable()
    {
        var unreachableClient = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=127.0.0.1;Port=5433;Database=nonexistent;Timeout=2;CommandTimeout=2;Username=postgres;Password=postgres");
        }).CreateClient();

        var response = await unreachableClient.GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(content);
        Assert.Equal("Unhealthy", content.Status);
    }
}

