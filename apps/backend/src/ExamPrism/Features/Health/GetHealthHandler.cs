namespace ExamPrism.Features.Health;

using System.Text.Json.Serialization;
using Common.Handlers;
using Common.Results;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public record GetHealthQuery;

public record GetHealthResult(
    string Status,
    DateTime Timestamp,
    [property: JsonIgnore] bool IsHealthy = true);

public sealed class GetHealthHandler(HealthCheckService healthCheckService) : IHandler<GetHealthQuery, GetHealthResult>
{
    public async Task<Result<GetHealthResult>> Handle(GetHealthQuery query, CancellationToken ct = default)
    {
        var report = await healthCheckService.CheckHealthAsync(ct);
        var isHealthy = report.Status == HealthStatus.Healthy;
        return new GetHealthResult(report.Status.ToString(), DateTime.UtcNow, isHealthy);
    }
}

