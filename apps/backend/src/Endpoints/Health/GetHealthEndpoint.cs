namespace ExamPrism.Endpoints.Health;

using Common.Endpoints;
using ExamPrism.Features.Health.GetHealth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class GetHealthEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", Handle)
            .WithTags("Health")
            .WithSummary("Returns health status of the API");
    }

    private static async Task<IResult> Handle(
        IMessageBus bus,
        CancellationToken ct)
    {
        var result = await bus.InvokeAsync<GetHealthResult>(new GetHealthQuery(), ct);
        return result.IsHealthy
            ? Results.Ok(result)
            : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
