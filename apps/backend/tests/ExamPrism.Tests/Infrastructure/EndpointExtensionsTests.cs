namespace ExamPrism.Tests.Infrastructure;

using Common.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Xunit;

public class EndpointExtensionsTests
{
    [Fact]
    public void MapEndpoints_DiscoversPublicAndExplicitIEndpointImplementations_AndIgnoresAbstractTypes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        using var app = builder.Build();

        app.MapEndpoints(typeof(EndpointExtensionsTests).Assembly);

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        routePatterns.Should().Contain("/test-discovery/public");
        routePatterns.Should().Contain("/test-discovery/explicit");
        routePatterns.Should().NotContain("/test-discovery/abstract");
    }

    [Fact]
    public void MapEndpoints_WhenEnvironmentIsNotDevelopment_SkipsEndpointsTestNamespace()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Production;
        using var app = builder.Build();

        app.MapEndpoints(typeof(EndpointExtensionsTests).Assembly);

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        routePatterns.Should().Contain("/test-discovery/public");
        routePatterns.Should().NotContain("/test-discovery/dev-only-fixture");
    }
}

public class PublicTestEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/test-discovery/public", () => Results.Ok("public"));
    }
}

public class ExplicitTestEndpoint : IEndpoint
{
    static void IEndpoint.MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/test-discovery/explicit", () => Results.Ok("explicit"));
    }
}

public abstract class AbstractTestEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/test-discovery/abstract", () => Results.Ok("abstract"));
    }
}
