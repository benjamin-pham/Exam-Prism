namespace ExamPrism.Infrastructure.Persistence;

using System;
using Common.MultiTenancy;
using Common.Persistence;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Interceptors;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

public static class PersistenceSetup
{
    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration,
        IHealthChecksBuilder healthChecksBuilder)
    {
        DapperTypeHandlers.EnsureRegistered();
        services.Configure<PersistenceOptions>(configuration.GetSection(PersistenceOptions.SectionName));
        services.AddSingleton<SlowQueryTelemetryInterceptor>();
        services.AddSingleton<AuditableEntityInterceptor>();
        services.AddScoped<DomainEventDispatchInterceptor>();
        services.AddSingleton<TenantDataSourceRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<TenantDataSourceRegistry>());

        return services;
    }

    internal static void ConfigureNpgsql(
        NpgsqlDbContextOptionsBuilder npgsqlOptions,
        PersistenceOptions persistenceOptions)
    {
        npgsqlOptions.ConfigureNpgsql(persistenceOptions);
    }
}
