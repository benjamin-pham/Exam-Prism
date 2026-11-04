namespace ExamPrism.Infrastructure;

using Common.Exceptions;
using ExamPrism.Infrastructure.Caching;
using ExamPrism.Infrastructure.Cors;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Localization;
using ExamPrism.Infrastructure.Logging;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.OpenApi;
using ExamPrism.Infrastructure.Orleans;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.RateLimiting;
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
        builder.Services.AddExamPrismCors(builder.Environment);

        builder.Host.UseExamPrismLogging();
        builder.Host.UseExamPrismWolverine();
        builder.Host.UseExamPrismOrleans();

        builder.Services.AddExamPrismOpenApi();
        builder.Services.AddExamPrismLocalization();
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

        services.AddExamPrismPersistence(configuration, healthChecksBuilder);
        services.AddExamPrismMultiTenancy(configuration);
        services.AddExamPrismCaching(configuration, healthChecksBuilder, out var useRedis);
        services.AddExamPrismRateLimiting(configuration, useRedis);
        services.AddExamPrismOrleans(configuration, healthChecksBuilder);
        services.AddExamPrismIdentity(configuration);

        return services;
    }
}
