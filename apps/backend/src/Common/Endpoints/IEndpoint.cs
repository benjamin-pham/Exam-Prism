namespace Common.Endpoints;

using Microsoft.AspNetCore.Routing;

/// <summary>
/// Compile-time contract for ASP.NET Core Minimal API endpoints.
/// Uses C# static abstract members for zero-allocation startup route registration.
/// </summary>
public interface IEndpoint
{
    static abstract void MapEndpoint(IEndpointRouteBuilder app);
}
