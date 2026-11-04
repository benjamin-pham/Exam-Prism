namespace ExamPrism.Infrastructure;

using Common.Locking;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class StartupTasksExtensions
{
    public static async Task InitializeInfrastructureAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        var distributedLock = app.Services.GetService<IDistributedLock>();
        var logger = app.Services.GetService<ILoggerFactory>()?.CreateLogger("StartupTasks");

        if (distributedLock is not null)
        {
            await using var handle = await distributedLock.TryAcquireAsync(
                "startup:infrastructure-init",
                TimeSpan.FromMinutes(5),
                cancellationToken);

            if (handle is null)
            {
                logger?.LogInformation("Startup initialization lock is held by another instance; skipping duplicate execution.");
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
}
