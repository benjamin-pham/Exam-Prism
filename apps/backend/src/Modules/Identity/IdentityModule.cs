namespace ExamPrism.Modules.Identity;

using Common.Modules;
using Common.MultiTenancy;
using Common.Persistence;
using ExamPrism.Domain.Identity;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Interceptors;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public sealed class IdentityModule : IModule
{
    public string Name => "Identity";

    public string[] ModuleSettingFiles => ["appsettings.identity.json"];

    public void ConfigureServices(WebApplicationBuilder builder)
    {
        ConfigureServices(builder.Services, builder.Configuration);
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddTenantIdentityModule(configuration);
    }
}

public static class IdentityModuleExtensions
{
    public static IServiceCollection AddTenantIdentityModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            var tenantInfo = sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>()
                .MultiTenantContext?.TenantInfo;

            if (tenantInfo is null || tenantInfo.Status != TenantStatus.Active || string.IsNullOrWhiteSpace(tenantInfo.ConnectionString))
            {
                throw new InvalidOperationException(
                    "An active tenant context with a valid connection string is required to resolve AppDbContext.");
            }

            var normalizer = sp.GetRequiredService<ITenantConnectionStringNormalizer>();
            var registry = sp.GetRequiredService<TenantDataSourceRegistry>();
            var persistenceOptions = sp.GetRequiredService<IOptions<PersistenceOptions>>().Value;
            var interceptor = sp.GetRequiredService<SlowQueryTelemetryInterceptor>();
            var auditableInterceptor = sp.GetRequiredService<AuditableEntityInterceptor>();
            var dispatchInterceptor = sp.GetRequiredService<DomainEventDispatchInterceptor>();

            var normalized = normalizer.Normalize(tenantInfo.ConnectionString);
            var dataSource = registry.GetOrCreate(normalized);

            options.AddInterceptors(interceptor, auditableInterceptor, dispatchInterceptor);
            options.UseNpgsql(dataSource, npgsqlOptions =>
                npgsqlOptions.ConfigureNpgsql(persistenceOptions));
        });

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        Action<IdentityOptions> configureIdentity = options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 6;
            options.User.RequireUniqueEmail = true;
        };

        services.AddIdentityCore<ApplicationUser>(configureIdentity)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        services.AddSingleton<IPasswordHasher<ApplicationUser>>(sp =>
            new BulkheadPasswordHasher<ApplicationUser>(
                sp.GetRequiredService<IOptions<IdentityHighLoadOptions>>()));

        services.AddScoped(sp => new ExamPrism.Features.Tenants.InitializeTenantIdentityHandler(
            sp.GetRequiredService<ITenantConnectionStringNormalizer>(),
            sp.GetRequiredService<IConnectionStringProtector>(),
            sp,
            sp.GetRequiredService<IOptions<PersistenceOptions>>()));

        services.AddScoped(sp => new ExamPrism.Features.Tenants.MigrateTenantDatabaseHandler(
            sp.GetRequiredService<ITenantConnectionStringNormalizer>(),
            sp.GetRequiredService<IOptions<PersistenceOptions>>()));

        return services;
    }
}
