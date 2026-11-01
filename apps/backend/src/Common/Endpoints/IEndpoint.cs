namespace Common.Endpoints;

using Microsoft.AspNetCore.Routing;

public interface IEndpoint
{
    static abstract void MapEndpoint(IEndpointRouteBuilder app);
}
