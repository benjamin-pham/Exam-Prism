namespace ExamPrism.Infrastructure.Batching;

using System;
using Common.Batching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class BatchingSetup
{
    public static IServiceCollection AddAppBatching(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (configuration != null)
        {
            services.Configure<BatchingOptions>(configuration.GetSection(BatchingOptions.SectionName));
        }
        else
        {
            services.AddOptions<BatchingOptions>();
        }

        return services;
    }

    public static IServiceCollection AddAppBatching(
        this IServiceCollection services,
        Action<BatchingOptions> configure)
    {
        services.Configure(configure);
        return services;
    }

    public static IServiceCollection AddWriteBehindBatcher<TItem, THandler>(
        this IServiceCollection services,
        Action<BatchingOptions>? configure = null)
        where THandler : class, IBatchFlushHandler<TItem>
    {
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.TryAddScoped<IBatchFlushHandler<TItem>, THandler>();
        services.TryAddScoped<THandler>();

        services.AddSingleton<ChannelWriteBehindBatcher<TItem>>();
        services.AddSingleton<IWriteBehindBatcher<TItem>>(sp => sp.GetRequiredService<ChannelWriteBehindBatcher<TItem>>());
        services.AddHostedService(sp => sp.GetRequiredService<ChannelWriteBehindBatcher<TItem>>());

        return services;
    }

    public static IServiceCollection AddWriteBehindBatcher<TItem, THandler>(
        this IServiceCollection services,
        BatchingOptions options)
        where THandler : class, IBatchFlushHandler<TItem>
    {
        services.TryAddScoped<IBatchFlushHandler<TItem>, THandler>();
        services.TryAddScoped<THandler>();

        services.AddSingleton(sp => new ChannelWriteBehindBatcher<TItem>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            options,
            sp.GetService<ILogger<ChannelWriteBehindBatcher<TItem>>>()));
        services.AddSingleton<IWriteBehindBatcher<TItem>>(sp => sp.GetRequiredService<ChannelWriteBehindBatcher<TItem>>());
        services.AddHostedService(sp => sp.GetRequiredService<ChannelWriteBehindBatcher<TItem>>());

        return services;
    }
}
