namespace ExamPrism.Infrastructure.Security;

using Common.Caching;
using Common.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

public static class DdosProtectionSetup
{
    public static IServiceCollection AddAppDdosProtection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<DdosProtectionOptions>(configuration.GetSection(DdosProtectionOptions.SectionName));

        services.TryAddSingleton<IIpReputationService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DdosProtectionOptions>>();
            var cache = sp.GetService<ICacheService>();
            var timeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            return new HybridIpReputationService(options, cache, timeProvider);
        });

        return services;
    }
}

