namespace ExamPrism.Tests.Features;

using System.Net;
using System.Text.Json.Nodes;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

public class ScalarApiDocsTests
{
    [Fact]
    public async Task GetScalarApiReference_InDevelopment_ReturnsOkAndHtml()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:CatalogConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
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
    public async Task GetScalarApiReference_OnTenantSubdomain_InDevelopment_ReturnsOk()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:CatalogConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
            builder.ConfigureServices(services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IMultiTenantStore<ExamPrismTenantInfo>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }
                var store = Substitute.For<IMultiTenantStore<ExamPrismTenantInfo>>();
                var tenantA = new ExamPrismTenantInfo(Guid.NewGuid().ToString("D"), "tenant-a")
                {
                    TenantGuid = Guid.NewGuid(),
                    Status = TenantStatus.Active
                };
                store.GetByIdentifierAsync("tenant-a").Returns(tenantA);
                services.AddSingleton(store);
            });
        });
        var client = factory.CreateClient();

        var response = await client.GetAsync("http://tenant-a.localhost/scalar/v1");

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
            builder.UseSetting("ConnectionStrings:CatalogConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
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
    public async Task GetOpenApiDocument_ContainsXTenantSecurityScheme()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:CatalogConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
        });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(json);
        node.Should().NotBeNull();

        var tenantScheme = node!["components"]?["securitySchemes"]?["X-Tenant"];
        tenantScheme.Should().NotBeNull();
        tenantScheme!["type"]?.ToString().Should().Be("apiKey");
        tenantScheme!["name"]?.ToString().Should().Be("X-Tenant");
        tenantScheme!["in"]?.ToString().Should().Be("header");
    }

    [Fact]
    public async Task GetOpenApiDocument_ContainsMultiTenantServerVariables()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:CatalogConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
        });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        var node = JsonNode.Parse(json);
        node.Should().NotBeNull();

        var servers = node!["servers"]?.AsArray();
        servers.Should().NotBeNull();
        var tenantServer = servers!.FirstOrDefault(s => s!["url"]?.ToString().Contains("{tenant}") == true);
        tenantServer.Should().NotBeNull();
        var tenantVar = tenantServer!["variables"]?["tenant"];
        tenantVar.Should().NotBeNull();
        tenantVar!["default"]?.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task OptionsRequest_FromLocalhostSubdomain_InDevelopment_ReturnsCorsHeaders()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:CatalogConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
        });
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/openapi/v1.json");
        request.Headers.Add("Origin", "http://school-a.localhost:5000");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain("http://school-a.localhost:5000");
    }

    [Fact]
    public async Task OptionsRequest_FromLocalhostSubdomain_InProduction_DoesNotReturnCorsHeaders()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:CatalogConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
        });
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/openapi/v1.json");
        request.Headers.Add("Origin", "http://school-a.localhost:5000");
        request.Headers.Add("Access-Control-Request-Method", "GET");

        var response = await client.SendAsync(request);

        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task GetScalarApiReference_InProduction_ReturnsNotFound()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:CatalogConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
        });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/scalar/v1");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetOpenApiDocument_InProduction_ReturnsNotFound()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:CatalogConnection", "Host=localhost;Database=dummy;Username=dummy;Password=dummy");
        });
        var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

