namespace ExamPrism.Infrastructure.Health;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class GracefulShutdownHostedService : IHostedService
{
    private readonly HostDrainState _drainState;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IOptions<GracefulShutdownOptions> _options;
    private readonly ILogger<GracefulShutdownHostedService> _logger;
    private CancellationTokenRegistration _stoppingRegistration;

    public GracefulShutdownHostedService(
        HostDrainState drainState,
        IHostApplicationLifetime lifetime,
        IOptions<GracefulShutdownOptions> options,
        ILogger<GracefulShutdownHostedService> logger)
    {
        _drainState = drainState;
        _lifetime = lifetime;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingRegistration = _lifetime.ApplicationStopping.Register(OnStopping);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingRegistration.Dispose();
        _drainState.MarkDraining();

        var delaySeconds = _options.Value.DrainDelaySeconds;
        if (delaySeconds > 0)
        {
            _logger.LogInformation("Waiting {DelaySeconds}s for reverse proxy/load balancer drain...", delaySeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Graceful shutdown drain delay was cancelled before completion.");
            }
        }
    }

    private void OnStopping()
    {
        _logger.LogInformation("Host is stopping. Marking HostDrainState.IsDraining = true");
        _drainState.MarkDraining();
    }
}
