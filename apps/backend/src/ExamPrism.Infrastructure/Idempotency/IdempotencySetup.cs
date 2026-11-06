namespace ExamPrism.Infrastructure.Idempotency;

using Common.Idempotency;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class IdempotencySetup
{
    public static IServiceCollection AddAppIdempotency(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<IdempotencyOptions>(configuration.GetSection(IdempotencyOptions.SectionName));
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<IdempotencyEndpointFilter>(sp));
        services.AddSingleton<IIdempotencyFilter>(sp => sp.GetRequiredService<IdempotencyEndpointFilter>());
        return services;
    }
}
