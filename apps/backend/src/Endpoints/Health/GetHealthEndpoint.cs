namespace ExamPrism.Endpoints.Health;

using Common.Endpoints;
using Common.Results;
using ExamPrism.Features.Health;
using ExamPrism.Infrastructure.Health;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class GetHealthEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () =>
        {
            return Results.Ok(new GetHealthResult("Healthy", DateTime.UtcNow, true));
        })
        .WithTags("Health")
        .WithSummary("Returns liveness status of the API")
        .DisableRateLimiting();

        var checkReadiness = async (
            HostDrainState drainState,
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (drainState.IsDraining)
            {
                return Results.Json(
                    new GetHealthResult("Draining", DateTime.UtcNow, false),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!drainState.IsStartupComplete)
            {
                return Results.Json(
                    new GetHealthResult("Starting", DateTime.UtcNow, false),
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var result = await bus.InvokeAsync<Result<GetHealthResult>>(new GetHealthQuery(), ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return result.Value.IsHealthy
                ? Results.Ok(result.Value)
                : Results.Json(result.Value, statusCode: StatusCodes.Status503ServiceUnavailable);
        };

        app.MapGet("/health/ready", checkReadiness)
            .WithTags("Health")
            .WithSummary("Returns readiness status of the API")
            .DisableRateLimiting();

        app.MapGet("/health", checkReadiness)
            .WithTags("Health")
            .WithSummary("Returns health status of the API")
            .DisableRateLimiting();
    }
}
