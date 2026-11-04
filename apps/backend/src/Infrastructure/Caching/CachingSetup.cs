namespace ExamPrism.Infrastructure.Caching;

using Common.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

public static class CachingSetup
{
    public static IServiceCollection AddExamPrismCaching(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecksBuilder,
        out bool useRedis)
    {
        var redisConnectionString = configuration.GetConnectionString("Redis");
        useRedis = !string.IsNullOrWhiteSpace(redisConnectionString);

        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));

        if (useRedis)
        {
            var redisOptions = ConfigurationOptions.Parse(redisConnectionString!);
            redisOptions.AbortOnConnectFail = false;

            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
            services.AddSingleton<IDistributedCache>(sp =>
            {
                var cacheOpts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
                var multiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
                var redisCache = new RedisCache(Options.Create(new RedisCacheOptions
                {
                    InstanceName = cacheOpts.KeyPrefix,
                    ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer)
                }));
                return new ResilientDistributedCache(redisCache, sp.GetRequiredService<ILogger<ResilientDistributedCache>>());
            });

            healthChecksBuilder.AddCheck<RedisHealthCheck>("redis");
        }

        var initialCacheOpts = configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new CacheOptions();
        services.AddHybridCache(options =>
        {
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(initialCacheOpts.DefaultExpirationMinutes),
                LocalCacheExpiration = TimeSpan.FromMinutes(initialCacheOpts.DefaultLocalExpirationMinutes)
            };
        });
        services.AddSingleton<ICacheService, HybridCacheService>();

        return services;
    }
}

