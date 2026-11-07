namespace ExamPrism.Modules.Tenants.Identity;

using System;
using System.Threading;
using System.Threading.Tasks;
using Common.Modules;
using ExamPrism.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class PlatformAdminStartupInitializer : IStartupInitializer
{
    public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var platformAdminOptions = services.GetService<IOptions<PlatformAdminOptions>>()?.Value;
        if (platformAdminOptions is not null && platformAdminOptions.SeedOnStartup)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var seeder = scope.ServiceProvider.GetRequiredService<PlatformAdminSeeder>();
                await seeder.SeedAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                var logger = services.GetService<ILogger<PlatformAdminStartupInitializer>>();
                logger?.LogWarning(ex, "PlatformAdminSeeder skipped on startup because database is not available or not yet migrated.");
            }
        }
    }
}
