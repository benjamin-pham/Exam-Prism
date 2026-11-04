namespace ExamPrism.Infrastructure.Http;

using System.IO.Compression;
using Common.Http;
using ExamPrism.Infrastructure.Caching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class HttpPerformanceSetup
{
    public static IServiceCollection AddAppHttpPerformance(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<HttpPerformanceOptions>(configuration.GetSection(HttpPerformanceOptions.SectionName));
        var timeoutOptions = configuration.GetSection(HttpPerformanceOptions.SectionName).Get<HttpPerformanceOptions>()
            ?? new HttpPerformanceOptions();

        services.AddRequestTimeouts(options =>
        {
            options.DefaultPolicy = new RequestTimeoutPolicy
            {
                Timeout = TimeSpan.FromSeconds(timeoutOptions.DefaultTimeoutSeconds)
            };

            options.AddPolicy(RequestTimeoutPolicies.Default, TimeSpan.FromSeconds(timeoutOptions.DefaultTimeoutSeconds));
            options.AddPolicy(RequestTimeoutPolicies.Fast, TimeSpan.FromSeconds(timeoutOptions.FastTimeoutSeconds));
            options.AddPolicy(RequestTimeoutPolicies.ExamSubmit, TimeSpan.FromSeconds(timeoutOptions.ExamSubmitTimeoutSeconds));
            options.AddPolicy(RequestTimeoutPolicies.LongRunning, TimeSpan.FromSeconds(timeoutOptions.LongRunningTimeoutSeconds));
        });

        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/problem+json"]);
        });

        services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        services.AddRequestDecompression();

        var cachingOptions = configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>();
        var outputCacheSeconds = cachingOptions?.OutputCacheExpirationSeconds ?? timeoutOptions.OutputCacheExpirationSeconds;

        services.AddOutputCache(options =>
        {
            options.AddPolicy("TenantScopedCache", builder =>
            {
                builder.Expire(TimeSpan.FromSeconds(outputCacheSeconds))
                    .SetVaryByHeader("X-Tenant", "Host", "Accept-Language");
            });
        });

        services.Configure<LoadSheddingOptions>(configuration.GetSection(LoadSheddingOptions.SectionName));
        services.AddSingleton(sp => new AdaptiveConcurrencyLimiter(sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LoadSheddingOptions>>()));
        services.AddAppRuntimeTuning(configuration);

        return services;
    }

    public static IApplicationBuilder UseAppHttpPerformance(this IApplicationBuilder app)
    {
        app.UseRequestDecompression();
        app.UseResponseCompression();
        app.UseAdaptiveLoadShedding();
        app.UseRequestTimeouts();
        app.UseOutputCache();
        return app;
    }
}
