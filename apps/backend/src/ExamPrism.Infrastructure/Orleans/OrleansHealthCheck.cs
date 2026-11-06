namespace ExamPrism.Infrastructure.Orleans;

using Microsoft.Extensions.Diagnostics.HealthChecks;
using global::Orleans.Runtime;

public sealed class OrleansHealthCheck : IHealthCheck
{
    private readonly ISiloStatusOracle? _siloStatusOracle;

    public OrleansHealthCheck(ISiloStatusOracle? siloStatusOracle = null)
    {
        _siloStatusOracle = siloStatusOracle;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_siloStatusOracle is null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Orleans silo status oracle is unavailable."));
        }

        var status = _siloStatusOracle.CurrentStatus;
        if (status == SiloStatus.Active)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Orleans silo is active."));
        }

        return Task.FromResult(HealthCheckResult.Unhealthy($"Orleans silo is not active (CurrentStatus: {status})."));
    }
}
