namespace ExamPrism.Infrastructure.MultiTenancy;

using Common.MultiTenancy;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure.Caching;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Wolverine;
using Wolverine.ErrorHandling;
using Wolverine.FluentValidation;

public static class MultiTenancySetup
{
    public static IHostBuilder UseWolverine(this IHostBuilder hostBuilder)
    {
        return hostBuilder.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(typeof(MultiTenancySetup).Assembly);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var name = asm.GetName().Name;
                if (name != null && name.StartsWith("ExamPrism.Modules.", StringComparison.OrdinalIgnoreCase))
                {
                    opts.Discovery.IncludeAssembly(asm);
                }
            }
            opts.UseFluentValidation();
            opts.Policies.AddMiddleware<TenantWolverineMiddleware>();
            opts.Policies.OnException<Npgsql.NpgsqlException>(ex => ex.IsTransient)
                .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250));
            opts.Policies.OnException<TimeoutException>()
                .RetryWithCooldown(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(150));

            opts.LocalQueue("critical").MaximumParallelMessages(Math.Max(4, Environment.ProcessorCount * 4));
            opts.LocalQueue("background").MaximumParallelMessages(Math.Max(2, Environment.ProcessorCount));
        });
    }

    public static IServiceCollection AddMultiTenancy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var busDescriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IMessageBus));
        if (busDescriptor != null)
        {
            services.Remove(busDescriptor);
            services.Add(new ServiceDescriptor(
                typeof(Wolverine.Runtime.MessageContext),
                busDescriptor.ImplementationType ?? typeof(Wolverine.Runtime.MessageContext),
                busDescriptor.Lifetime));
            services.Add(new ServiceDescriptor(
                typeof(IMessageBus),
                sp => new MultiTenantMessageBus(
                    sp.GetRequiredService<Wolverine.Runtime.MessageContext>(),
                    sp.GetRequiredService<IMultiTenantStore<ExamPrismTenantInfo>>(),
                    sp.GetRequiredService<IMultiTenantContextSetter>(),
                    sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>()),
                busDescriptor.Lifetime));
        }

        services.Configure<MultiTenancyOptions>(configuration.GetSection(MultiTenancyOptions.SectionName));
        services.AddDataProtection()
            .SetApplicationName("ExamPrism");

        var redisConnectionString = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp =>
                new ConfigureNamedOptions<KeyManagementOptions>(Options.DefaultName, options =>
                {
                    var multiplexer = sp.GetService<IConnectionMultiplexer>();
                    if (multiplexer is not null)
                    {
                        var cacheOpts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
                        var logger = sp.GetService<ILogger<RedisXmlRepository>>();
                        options.XmlRepository = new RedisXmlRepository(
                            multiplexer,
                            $"{cacheOpts.KeyPrefix}dataprotection:keys",
                            logger);
                    }
                }));
        }

        services.AddSingleton<IConnectionStringProtector, DataProtectionConnectionStringProtector>();
        services.AddSingleton<ITenantConnectionStringNormalizer, TenantConnectionStringNormalizer>();
        services.AddSingleton<TenantCatalogShieldState>();
        services.AddScoped<ITenantContext, TenantContext>();

        return services;
    }

    public static async Task InitializeMultiTenancyAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        var multiTenancyOptions = app.Services.GetService<IOptions<MultiTenancyOptions>>()?.Value;
        if (multiTenancyOptions is null)
        {
            return;
        }

        if (multiTenancyOptions.Provisioning.RecoverStuckProvisioningOnStartup)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var catalogDb = scope.ServiceProvider.GetService<CatalogDbContext>();
                var bus = scope.ServiceProvider.GetService<IMessageBus>();
                if (catalogDb is not null && bus is not null)
                {
                    var stuckTenants = await catalogDb.Tenants
                        .Where(t => t.Status == TenantStatus.Provisioning)
                        .Select(t => t.Id)
                        .ToListAsync(cancellationToken);

                    foreach (var tenantId in stuckTenants)
                    {
                        await bus.SendAsync(new StartTenantProvisioningCommand(tenantId));
                    }
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Stuck provisioning recovery skipped on startup because database or bus is not available.");
            }
        }

        if (multiTenancyOptions.FleetMigration.MigrateOnStartup)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var fleetMigrator = scope.ServiceProvider.GetService<ITenantFleetMigrator>();
                if (fleetMigrator is not null)
                {
                    await fleetMigrator.MigrateAllActiveTenantsAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                app.Logger.LogWarning(ex, "Fleet migration skipped on startup because database is not available.");
            }
        }
    }
}
