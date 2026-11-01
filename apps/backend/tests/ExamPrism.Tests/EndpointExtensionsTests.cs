namespace Tests;

using System.Reflection;
using Common.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Xunit;

public class EndpointExtensionsTests
{
    public class MockEndpointRouteBuilder : IEndpointRouteBuilder
    {
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotImplementedException();
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IServiceProvider ServiceProvider => null!;
    }

    public class StandardTestEndpoint : IEndpoint
    {
        public static bool Invoked { get; set; }
        public static void MapEndpoint(IEndpointRouteBuilder app) => Invoked = true;
    }

    public class ExplicitTestEndpoint : IEndpoint
    {
        public static bool Invoked { get; set; }
        static void IEndpoint.MapEndpoint(IEndpointRouteBuilder app) => Invoked = true;
    }

    public abstract class AbstractTestEndpoint : IEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app) => throw new InvalidOperationException("Should not be called");
    }

    public class NonEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app) => throw new InvalidOperationException("Should not be called");
    }

    [Fact]
    public void MapEndpoints_InvokesRegisteredEndpoints()
    {
        StandardTestEndpoint.Invoked = false;
        ExplicitTestEndpoint.Invoked = false;
        var builder = new MockEndpointRouteBuilder();

        builder.MapEndpoints(typeof(EndpointExtensionsTests).Assembly);

        Assert.True(StandardTestEndpoint.Invoked);
        Assert.True(ExplicitTestEndpoint.Invoked);
    }

    [Fact]
    public void MapEndpoints_GenericMarker_InvokesRegisteredEndpoints()
    {
        StandardTestEndpoint.Invoked = false;
        var builder = new MockEndpointRouteBuilder();

        builder.MapEndpoints<EndpointExtensionsTests>();

        Assert.True(StandardTestEndpoint.Invoked);
    }
}
