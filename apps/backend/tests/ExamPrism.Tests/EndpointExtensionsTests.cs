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
        public static int InvocationCount { get; set; }
        public static void MapEndpoint(IEndpointRouteBuilder app) => InvocationCount++;
    }

    public class ExplicitTestEndpoint : IEndpoint
    {
        public static int InvocationCount { get; set; }
        static void IEndpoint.MapEndpoint(IEndpointRouteBuilder app) => InvocationCount++;
    }

    internal class InternalTestEndpoint : IEndpoint
    {
        public static int InvocationCount { get; set; }
        public static void MapEndpoint(IEndpointRouteBuilder app) => InvocationCount++;
    }

    public class ThrowingTestEndpoint : IEndpoint
    {
        public static bool ShouldThrow { get; set; }
        public static void MapEndpoint(IEndpointRouteBuilder app)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Endpoint registration failure");
            }
        }
    }

    public class OpenGenericEndpoint<T> : IEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app) { }
    }

    public abstract class AbstractTestEndpoint : IEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app) => throw new InvalidOperationException("Should not be called");
    }

    public class NonEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app) => throw new InvalidOperationException("Should not be called");
    }

    private void ResetCounters()
    {
        StandardTestEndpoint.InvocationCount = 0;
        ExplicitTestEndpoint.InvocationCount = 0;
        InternalTestEndpoint.InvocationCount = 0;
        ThrowingTestEndpoint.ShouldThrow = false;
    }

    [Fact]
    public void MapEndpoints_InvokesRegisteredEndpoints()
    {
        ResetCounters();
        var builder = new MockEndpointRouteBuilder();

        builder.MapEndpoints(typeof(EndpointExtensionsTests).Assembly);

        Assert.Equal(1, StandardTestEndpoint.InvocationCount);
        Assert.Equal(1, ExplicitTestEndpoint.InvocationCount);
        Assert.Equal(1, InternalTestEndpoint.InvocationCount);
    }

    [Fact]
    public void MapEndpoints_DuplicateAssemblies_InvokesEachEndpointOnlyOnce()
    {
        ResetCounters();
        var builder = new MockEndpointRouteBuilder();
        var asm = typeof(EndpointExtensionsTests).Assembly;

        builder.MapEndpoints(asm, asm);

        Assert.Equal(1, StandardTestEndpoint.InvocationCount);
        Assert.Equal(1, ExplicitTestEndpoint.InvocationCount);
        Assert.Equal(1, InternalTestEndpoint.InvocationCount);
    }

    [Fact]
    public void MapEndpoints_GenericMarker_InvokesRegisteredEndpoints()
    {
        ResetCounters();
        var builder = new MockEndpointRouteBuilder();

        builder.MapEndpoints<EndpointExtensionsTests>();

        Assert.Equal(1, StandardTestEndpoint.InvocationCount);
        Assert.Equal(1, ExplicitTestEndpoint.InvocationCount);
        Assert.Equal(1, InternalTestEndpoint.InvocationCount);
    }

    [Fact]
    public void MapEndpoints_NullApp_ThrowsArgumentNullException()
    {
        IEndpointRouteBuilder builder = null!;
        Assert.Throws<ArgumentNullException>(() => builder.MapEndpoints());
    }

    [Fact]
    public void MapEndpoints_NullAssembliesArray_ThrowsArgumentNullException()
    {
        var builder = new MockEndpointRouteBuilder();
        Assert.Throws<ArgumentNullException>(() => builder.MapEndpoints(assemblies: null!));
    }

    [Fact]
    public void MapEndpoints_WhenEndpointThrows_UnwrapsTargetInvocationException()
    {
        ResetCounters();
        ThrowingTestEndpoint.ShouldThrow = true;
        var builder = new MockEndpointRouteBuilder();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.MapEndpoints(typeof(EndpointExtensionsTests).Assembly));
        Assert.Equal("Endpoint registration failure", ex.Message);
    }
}
