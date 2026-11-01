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

    [Fact]
    public void MapEndpoints_NullApp_ThrowsArgumentNullException()
    {
        IEndpointRouteBuilder app = null!;
        var act = () => app.MapEndpoints();
        act.Should().Throw<ArgumentNullException>().WithParameterName("app");
    }

    [Fact]
    public void MapEndpoints_NullAssembliesArray_ThrowsArgumentNullException()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        var act = () => app.MapEndpoints(assemblies: null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("assemblies");
    }

    [Fact]
    public void MapEndpoints_GenericMarker_DiscoversEndpoints()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        app.MapEndpoints<EndpointExtensionsTests>();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .ToList();

        routePatterns.Should().Contain("/test-discovery/public");
    }

    [Fact]
    public void MapEndpoints_DuplicateAssemblies_DeduplicatesWithoutError()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();

        var asm = typeof(EndpointExtensionsTests).Assembly;
        app.MapEndpoints(asm, asm);

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText)
            .Where(r => r == "/test-discovery/public")
            .ToList();

        routePatterns.Count.Should().Be(1);
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
