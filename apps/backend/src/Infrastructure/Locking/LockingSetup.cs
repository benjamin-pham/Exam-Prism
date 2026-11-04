namespace ExamPrism.Infrastructure.Locking;

using Common.Locking;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class LockingSetup
{
    public static IServiceCollection AddLocking(
        this IServiceCollection services,
        IConfiguration configuration,
        bool useRedis)
    {
        services.Configure<LockingOptions>(configuration.GetSection(LockingOptions.SectionName));

        if (useRedis)
        {
            services.AddSingleton<IDistributedLock, RedisDistributedLock>();
        }
        else
        {
            services.AddSingleton<IDistributedLock, InMemoryDistributedLock>();
        }

        return services;
    }
}
