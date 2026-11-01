namespace ExamPrism.Features.Health;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Wolverine.Http;

public record HealthResponse(string Status, DateTime Timestamp);

public static class HealthHandler
{
    [WolverineGet("/health")]
    [Tags("Health")]
    [EndpointSummary("Returns health status of the API")]
    public static async Task<IResult> Handle(HealthCheckService healthCheckService)
    {
        var report = await healthCheckService.CheckHealthAsync();
        var status = report.Status.ToString();
        var response = new HealthResponse(status, DateTime.UtcNow);

        return report.Status == HealthStatus.Healthy
            ? Results.Ok(response)
            : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
