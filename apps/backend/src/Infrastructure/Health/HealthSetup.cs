namespace ExamPrism.Infrastructure.Health;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class HealthSetup
{
    public static IServiceCollection AddAppHealthDrain(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration is not null)
        {
            services.Configure<GracefulShutdownOptions>(
                configuration.GetSection(GracefulShutdownOptions.SectionName));
        }

        services.TryAddSingleton<HostDrainState>();
        services.AddHostedService<GracefulShutdownHostedService>();

        return services;
    }
}
