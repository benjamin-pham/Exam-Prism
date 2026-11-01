namespace Common.Endpoints;

using Microsoft.AspNetCore.Routing;

/// <summary>
/// Defines a Minimal API endpoint or endpoint group.
/// Implemented using static abstract interface members to eliminate object allocations during startup registration.
/// </summary>
public interface IEndpoint
{
    /// <summary>
    /// Configures routes, middleware, and metadata on the provided <see cref="IEndpointRouteBuilder"/>.
    /// </summary>
    /// <param name="app">The route builder to map endpoints to.</param>
    static abstract void MapEndpoint(IEndpointRouteBuilder app);
}
