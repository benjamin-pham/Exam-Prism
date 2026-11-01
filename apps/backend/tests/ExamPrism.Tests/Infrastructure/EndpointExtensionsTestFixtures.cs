namespace ExamPrism.Endpoints.Test;

using Common.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public class DevOnlyFixtureEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/test-discovery/dev-only-fixture", () => Results.Ok("dev-only"));
    }
}

