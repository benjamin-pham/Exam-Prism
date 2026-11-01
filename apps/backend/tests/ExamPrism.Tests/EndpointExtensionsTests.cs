namespace Tests;

using System.Reflection;
using Common.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Xunit;

public class EndpointExtensionsTests : IDisposable
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

    public class OuterGeneric<T>
    {
        public class NestedEndpoint : IEndpoint
        {
            public static void MapEndpoint(IEndpointRouteBuilder app) { }
        }
    }

    public abstract class AbstractTestEndpoint : IEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app) => throw new InvalidOperationException("Should not be called");
    }

    public class NonEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app) => throw new InvalidOperationException("Should not be called");
    }

    public EndpointExtensionsTests()
    {
        ResetCounters();
    }

    public void Dispose()
    {
        ResetCounters();
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
        var builder = new MockEndpointRouteBuilder();

        builder.MapEndpoints(typeof(EndpointExtensionsTests).Assembly);

        StandardTestEndpoint.InvocationCount.Should().Be(1);
        ExplicitTestEndpoint.InvocationCount.Should().Be(1);
        InternalTestEndpoint.InvocationCount.Should().Be(1);
    }

    [Fact]
    public void MapEndpoints_Parameterless_DiscoversExecutingAndCallingAssemblies()
    {
        var builder = new MockEndpointRouteBuilder();

        builder.MapEndpoints();

        StandardTestEndpoint.InvocationCount.Should().Be(1);
        ExplicitTestEndpoint.InvocationCount.Should().Be(1);
        InternalTestEndpoint.InvocationCount.Should().Be(1);
    }

    [Fact]
    public void MapEndpoints_DuplicateAssemblies_InvokesEachEndpointOnlyOnce()
    {
        var builder = new MockEndpointRouteBuilder();
        var asm = typeof(EndpointExtensionsTests).Assembly;

        builder.MapEndpoints(asm, asm);

        StandardTestEndpoint.InvocationCount.Should().Be(1);
        ExplicitTestEndpoint.InvocationCount.Should().Be(1);
        InternalTestEndpoint.InvocationCount.Should().Be(1);
    }

    [Fact]
    public void MapEndpoints_GenericMarker_InvokesRegisteredEndpoints()
    {
        var builder = new MockEndpointRouteBuilder();

        builder.MapEndpoints<EndpointExtensionsTests>();

        StandardTestEndpoint.InvocationCount.Should().Be(1);
        ExplicitTestEndpoint.InvocationCount.Should().Be(1);
        InternalTestEndpoint.InvocationCount.Should().Be(1);
    }

    [Fact]
    public void MapEndpoints_NullApp_ThrowsArgumentNullException()
    {
        IEndpointRouteBuilder builder = null!;
        Action act = () => builder.MapEndpoints();
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("app");
    }

    [Fact]
    public void MapEndpoints_NullAssembliesArray_ThrowsArgumentNullException()
    {
        var builder = new MockEndpointRouteBuilder();
        Action act = () => builder.MapEndpoints(assemblies: null!);
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("assemblies");
    }

    [Fact]
    public void MapEndpoints_WhenEndpointThrows_UnwrapsTargetInvocationException()
    {
        try
        {
            ThrowingTestEndpoint.ShouldThrow = true;
            var builder = new MockEndpointRouteBuilder();

            Action act = () => builder.MapEndpoints(typeof(EndpointExtensionsTests).Assembly);
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("Endpoint registration failure");
        }
        finally
        {
            ThrowingTestEndpoint.ShouldThrow = false;
        }
    }
}
