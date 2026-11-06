namespace ExamPrism.Infrastructure.Http;

using System;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class RuntimeTuningSetup
{
    public static bool ApplyThreadPoolTuning(RuntimeTuningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        ThreadPool.GetMinThreads(out var curWorker, out var curIo);
        int targetWorker = Math.Max(curWorker, options.MinWorkerThreads);
        int targetIo = Math.Max(curIo, options.MinCompletionPortThreads);

        return ThreadPool.SetMinThreads(targetWorker, targetIo);
    }

    public static IServiceCollection AddAppRuntimeTuning(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<RuntimeTuningOptions>(configuration.GetSection(RuntimeTuningOptions.SectionName));

        var options = configuration.GetSection(RuntimeTuningOptions.SectionName).Get<RuntimeTuningOptions>()
            ?? new RuntimeTuningOptions();

        ApplyThreadPoolTuning(options);

        return services;
    }

    public static WebApplicationBuilder ConfigureAppKestrel(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(RuntimeTuningOptions.SectionName).Get<RuntimeTuningOptions>()
            ?? new RuntimeTuningOptions();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (options.MaxConcurrentConnections.HasValue)
            {
                kestrel.Limits.MaxConcurrentConnections = options.MaxConcurrentConnections.Value;
            }

            if (options.MaxRequestBodySize.HasValue)
            {
                kestrel.Limits.MaxRequestBodySize = options.MaxRequestBodySize.Value;
            }

            kestrel.Limits.Http2.MaxStreamsPerConnection = options.MaxHttp2StreamsPerConnection;
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(options.KeepAliveTimeoutSeconds);
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(options.RequestHeadersTimeoutSeconds);

            if (options.MinRequestBodyDataRateBytesPerSecond > 0)
            {
                kestrel.Limits.MinRequestBodyDataRate = new MinDataRate(
                    options.MinRequestBodyDataRateBytesPerSecond,
                    TimeSpan.FromSeconds(options.DataRateGracePeriodSeconds));
            }

            if (options.MinResponseDataRateBytesPerSecond > 0)
            {
                kestrel.Limits.MinResponseDataRate = new MinDataRate(
                    options.MinResponseDataRateBytesPerSecond,
                    TimeSpan.FromSeconds(options.DataRateGracePeriodSeconds));
            }

            kestrel.Limits.MaxRequestHeaderCount = options.MaxRequestHeaderCount;
            kestrel.Limits.MaxRequestHeadersTotalSize = options.MaxRequestHeadersTotalSizeBytes;
            kestrel.Limits.MaxRequestLineSize = options.MaxRequestLineSizeBytes;
        });

        return builder;
    }
}
