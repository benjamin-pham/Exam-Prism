namespace ExamPrism.Infrastructure.Resilience;

using Common.Resilience;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

public static class ResilienceSetup
{
    public static IServiceCollection AddAppResilience(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ResilienceOptions>(configuration.GetSection(ResilienceOptions.SectionName));
        var resilienceOptions = configuration.GetSection(ResilienceOptions.SectionName).Get<ResilienceOptions>()
            ?? new ResilienceOptions();

        services.AddResiliencePipeline(ResiliencePipelineNames.DefaultIo, (builder, context) =>
        {
            builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = resilienceOptions.MaxRetryAttempts,
                    Delay = TimeSpan.FromSeconds(resilienceOptions.BackoffBaseDelaySeconds),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = resilienceOptions.CircuitBreakerFailureRatio,
                    SamplingDuration = TimeSpan.FromSeconds(resilienceOptions.CircuitBreakerSamplingDurationSeconds),
                    BreakDuration = TimeSpan.FromSeconds(resilienceOptions.CircuitBreakerBreakDurationSeconds),
                    MinimumThroughput = resilienceOptions.CircuitBreakerMinimumThroughput
                })
                .AddTimeout(new TimeoutStrategyOptions
                {
                    Timeout = TimeSpan.FromSeconds(resilienceOptions.TimeoutSeconds)
                });
        });

        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(options =>
            options.Retry.DisableForUnsafeHttpMethods()));

        return services;
    }
}
