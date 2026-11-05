namespace ExamPrism.Infrastructure;

using System.Diagnostics;
using Common.Locking;
using ExamPrism.Infrastructure.Health;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public static class StartupTasksExtensions
{
    public static async Task InitializeInfrastructureAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        var drainState = app.Services.GetService<HostDrainState>();
        var distributedLock = app.Services.GetService<IDistributedLock>();
        var logger = (ILogger?)app.Services.GetService<ILoggerFactory>()?.CreateLogger("StartupTasks") ?? NullLogger.Instance;
        var shutdownOpts = app.Services.GetService<IOptions<GracefulShutdownOptions>>()?.Value;
        var timeout = TimeSpan.FromSeconds(shutdownOpts?.StartupWaitTimeoutSeconds ?? 30);
        var pollInterval = TimeSpan.FromMilliseconds(shutdownOpts?.StartupPollIntervalMs ?? 200);

        drainState?.MarkStartupIncomplete();
        try
        {
            if (distributedLock is not null)
            {
                await using var handle = await distributedLock.TryAcquireAsync(
                    "startup:infrastructure-init",
                    TimeSpan.FromMinutes(5),
                    cancellationToken);

                if (handle is null)
                {
                    logger.LogInformation("Startup initialization lock is held by another instance; awaiting leader completion.");
                    await WaitForLeaderStartupAsync(distributedLock, timeout, pollInterval, logger, cancellationToken);
                    return;
                }

                await app.InitializeIdentityAsync(cancellationToken);
                await app.InitializeMultiTenancyAsync(cancellationToken);
            }
            else
            {
                await app.InitializeIdentityAsync(cancellationToken);
                await app.InitializeMultiTenancyAsync(cancellationToken);
            }
        }
        finally
        {
            drainState?.MarkStartupComplete();
        }
    }

    public static async Task<bool> WaitForLeaderStartupAsync(
        IDistributedLock distributedLock,
        TimeSpan timeout,
        TimeSpan pollInterval,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested && Stopwatch.GetElapsedTime(start) < timeout)
        {
            var probeHandle = await distributedLock.TryAcquireAsync(
                "startup:infrastructure-init",
                TimeSpan.FromSeconds(5),
                cancellationToken);

            if (probeHandle is not null)
            {
                await probeHandle.DisposeAsync();
                logger.LogInformation("Leader startup lock released; follower startup barrier cleared.");
                return true;
            }

            try
            {
                await Task.Delay(pollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogWarning("Timed out or cancelled waiting for leader startup lock to be released after {Timeout}s.", timeout.TotalSeconds);
        return false;
    }
}
