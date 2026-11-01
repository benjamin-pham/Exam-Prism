namespace ExamPrism.Endpoints.Health;

using Common.Endpoints;
using Common.Results;
using ExamPrism.Features.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class GetHealthEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<GetHealthResult>>(new GetHealthQuery(), ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return result.Value.IsHealthy
                ? Results.Ok(result.Value)
                : Results.Json(result.Value, statusCode: StatusCodes.Status503ServiceUnavailable);
        })
        .WithTags("Health")
        .WithSummary("Returns health status of the API");
    }
}
