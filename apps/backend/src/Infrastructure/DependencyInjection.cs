namespace ExamPrism.Infrastructure;

using Common.Exceptions;
using ExamPrism.Infrastructure.Batching;
using ExamPrism.Infrastructure.Caching;
using ExamPrism.Infrastructure.Cors;
using ExamPrism.Infrastructure.Health;
using ExamPrism.Infrastructure.Http;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Idempotency;
using ExamPrism.Infrastructure.Localization;
using ExamPrism.Infrastructure.Locking;
using ExamPrism.Infrastructure.Logging;
using ExamPrism.Infrastructure.Modules;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.OpenApi;
using ExamPrism.Infrastructure.Orleans;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.RateLimiting;
using ExamPrism.Infrastructure.Resilience;
using ExamPrism.Infrastructure.Security;
using ExamPrism.Infrastructure.Telemetry;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

public static class DependencyInjection
{
    public const string DevCorsPolicyName = CorsSetup.DevCorsPolicyName;

    public static WebApplicationBuilder AddInfrastructure(this WebApplicationBuilder builder)
    {
        builder.AddModules();
        builder.ConfigureAppKestrel();
        builder.Services.AddAppCors(builder.Environment);

        builder.Host.UseLogging();
        builder.Host.UseWolverine();
        builder.Host.UseOrleans();

        builder.Services.AddAppOpenApi();
        builder.Services.AddAppLocalization();
        builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);

        return builder;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging();
        services.TryAddSingleton(configuration);
        services.TryAddSingleton(TimeProvider.System);

        var healthChecksBuilder = services.AddHealthChecks();

        services.AddAppReverseProxy(configuration);
        services.AddAppDdosProtection(configuration);
        services.AddAppHttpPerformance(configuration);
        services.AddAppBatching(configuration);
        services.AddAppIdempotency(configuration);
        services.AddAppHealthDrain(configuration);
        services.AddPersistence(configuration, healthChecksBuilder);
        services.AddMultiTenancy(configuration);
        services.AddCaching(configuration, healthChecksBuilder, out var useRedis);
        services.AddLocking(configuration, useRedis);
        services.AddRateLimiting(configuration, useRedis);
        services.AddOrleans(configuration, healthChecksBuilder);
        services.AddIdentity(configuration);
        services.AddAppResilience(configuration);
        services.AddAppTelemetry(configuration);

        return services;
    }
}
