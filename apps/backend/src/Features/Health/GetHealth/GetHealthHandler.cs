namespace ExamPrism.Features.Health.GetHealth;

using Common.Handlers;
using Microsoft.Extensions.Diagnostics.HealthChecks;

public sealed class GetHealthHandler : IHandler
{
    public static async Task<GetHealthResult> Handle(
        GetHealthQuery query,
        HealthCheckService healthCheckService,
        CancellationToken ct = default)
    {
        var report = await healthCheckService.CheckHealthAsync(ct);
        var isHealthy = report.Status == HealthStatus.Healthy;
        return new GetHealthResult(report.Status.ToString(), DateTime.UtcNow, isHealthy);
    }
}
