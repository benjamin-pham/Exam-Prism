namespace ExamPrism.Modules.Tenants;

using System;
using Common.Modules;
using Common.MultiTenancy;
using Common.Persistence;
using Common.Security;
using ExamPrism.Domain.Identity;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using ExamPrism.Infrastructure.Persistence.Interceptors;
using ExamPrism.Modules.Tenants.Identity;
using ExamPrism.Modules.Tenants.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wolverine;

public sealed class TenantsModule : IModule
{
    public string Name => "Tenants";

    public string[] ModuleSettingFiles => ["appsettings.tenants.json"];

    public void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddTenantsModule(builder.Configuration);
    }
}

public static class TenantsModuleExtensions
{
    public static IServiceCollection AddTenantsModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        DapperTypeHandlers.EnsureRegistered();

        var catalogConnection = configuration.GetConnectionString("CatalogConnection")
            ?? throw new InvalidOperationException("Connection string 'CatalogConnection' not found.");

        services.AddDbContext<CatalogDbContext>((sp, options) =>
        {
            var persistenceOptions = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            var interceptor = sp.GetRequiredService<SlowQueryTelemetryInterceptor>();
            var auditableInterceptor = sp.GetRequiredService<AuditableEntityInterceptor>();
            var dispatchInterceptor = sp.GetRequiredService<DomainEventDispatchInterceptor>();

            options.AddInterceptors(interceptor, auditableInterceptor, dispatchInterceptor);
            options.UseNpgsql(catalogConnection, npgsqlOptions =>
                npgsqlOptions.ConfigureNpgsql(persistenceOptions));
        });

        services.AddScoped<ICatalogDbContext>(sp => sp.GetRequiredService<CatalogDbContext>());

        services.AddHealthChecks().AddDbContextCheck<CatalogDbContext>(name: "catalog_db");

        Action<IdentityOptions> configureIdentity = options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 6;
            options.User.RequireUniqueEmail = true;
        };

        services.AddIdentityCore<PlatformUser>(configureIdentity)
            .AddRoles<PlatformRole>()
            .AddEntityFrameworkStores<CatalogDbContext>()
            .AddDefaultTokenProviders();

        services.AddSingleton<IPasswordHasher<PlatformUser>>(sp =>
            new BulkheadPasswordHasher<PlatformUser>(
                sp.GetRequiredService<IOptions<IdentityHighLoadOptions>>()));

        services.Configure<PlatformAdminOptions>(configuration.GetSection(PlatformAdminOptions.SectionName));
        services.AddScoped<PlatformAdminSeeder>();

        services.AddScoped<CatalogMultiTenantStore>();
        services.AddScoped<ITenantCacheInvalidator>(sp => sp.GetRequiredService<CatalogMultiTenantStore>());

        services.AddScoped<ITenantProvisioner>(sp => new TenantProvisioner(
            sp.GetRequiredService<CatalogDbContext>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<MultiTenancyOptions>>(),
            sp.GetRequiredService<IConnectionStringProtector>(),
            sp.GetRequiredService<ITenantConnectionStringNormalizer>(),
            sp.GetRequiredService<ITenantCacheInvalidator>(),
            sp.GetService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<TenantProvisioner>>(),
            sp.GetService<IMessageBus>()));

        services.AddScoped<ITenantFleetMigrator>(sp => new TenantFleetMigrator(
            sp.GetRequiredService<CatalogDbContext>(),
            sp.GetRequiredService<IConnectionStringProtector>(),
            sp.GetRequiredService<ITenantConnectionStringNormalizer>(),
            sp.GetRequiredService<IOptions<MultiTenancyOptions>>(),
            sp.GetRequiredService<ILogger<TenantFleetMigrator>>(),
            sp.GetService<IServiceScopeFactory>(),
            sp.GetService<IMessageBus>()));

        services.AddMultiTenant<ExamPrismTenantInfo>()
            .WithStrategy<SubdomainMultiTenantStrategy>(ServiceLifetime.Scoped)
            .WithStore<CatalogMultiTenantStore>(ServiceLifetime.Scoped);

        services.AddSingleton<IStartupInitializer, PlatformAdminStartupInitializer>();
        services.AddSingleton<IStartupInitializer, MultiTenancyStartupInitializer>();

        return services;
    }
}
