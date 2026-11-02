namespace ExamPrism.Infrastructure;

using System.Text;
using Common.Caching;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.Caching;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using ExamPrism.Infrastructure.RateLimiting;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.Extensions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;
using Wolverine;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLogging();

        var catalogConnection = configuration.GetConnectionString("CatalogConnection")
            ?? throw new InvalidOperationException("Connection string 'CatalogConnection' not found.");

        services.AddDbContext<CatalogDbContext>(options =>
            options.UseNpgsql(catalogConnection));

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
        services.AddDataProtection();
        services.AddSingleton<IConnectionStringProtector, DataProtectionConnectionStringProtector>();
        services.AddSingleton<ITenantConnectionStringNormalizer, TenantConnectionStringNormalizer>();
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<CatalogMultiTenantStore>();
        services.AddScoped<ITenantCacheInvalidator>(sp => sp.GetRequiredService<CatalogMultiTenantStore>());

        services.AddMultiTenant<ExamPrismTenantInfo>()
            .WithStrategy<SubdomainMultiTenantStrategy>(ServiceLifetime.Scoped)
            .WithStore<CatalogMultiTenantStore>(ServiceLifetime.Scoped);

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
            options.UseNpgsql(normalizer.Normalize(tenantInfo.ConnectionString));
        });

        var healthChecksBuilder = services.AddHealthChecks()
            .AddDbContextCheck<CatalogDbContext>(name: "catalog_db");

        var redisConnectionString = configuration.GetConnectionString("Redis");
        var useRedis = !string.IsNullOrWhiteSpace(redisConnectionString);

        services.Configure<CacheOptions>(configuration.GetSection(CacheOptions.SectionName));
        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.SectionName));

        if (useRedis)
        {
            var redisOptions = ConfigurationOptions.Parse(redisConnectionString!);
            redisOptions.AbortOnConnectFail = false;

            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
            services.AddSingleton<IDistributedCache>(sp =>
            {
                var cacheOpts = sp.GetRequiredService<IOptions<CacheOptions>>().Value;
                var multiplexer = sp.GetRequiredService<IConnectionMultiplexer>();
                var redisCache = new RedisCache(Options.Create(new RedisCacheOptions
                {
                    InstanceName = cacheOpts.KeyPrefix,
                    ConnectionMultiplexerFactory = () => Task.FromResult(multiplexer)
                }));
                return new ResilientDistributedCache(redisCache, sp.GetRequiredService<ILogger<ResilientDistributedCache>>());
            });

            healthChecksBuilder.AddCheck<RedisHealthCheck>("redis");
        }

        services.TryAddSingleton(TimeProvider.System);

        var initialCacheOpts = configuration.GetSection(CacheOptions.SectionName).Get<CacheOptions>() ?? new CacheOptions();
        services.AddHybridCache(options =>
        {
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(initialCacheOpts.DefaultExpirationMinutes),
                LocalCacheExpiration = TimeSpan.FromMinutes(initialCacheOpts.DefaultLocalExpirationMinutes)
            };
        });
        services.AddSingleton<ICacheService, HybridCacheService>();

        services.AddExamPrismRateLimiting(configuration, useRedis);

        services.AddHttpContextAccessor();
        services.AddScoped<Common.Security.IUserContext, UserContext>();

        // Identity setup
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 6;
            options.User.RequireUniqueEmail = true;
        })
        .AddRoles<IdentityRole<Guid>>()
        .AddEntityFrameworkStores<AppDbContext>()
        .AddDefaultTokenProviders();

        // JwtOptions & TokenService
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddScoped<ITokenService, TokenService>();
        services.AddSingleton<JwtTenantValidator>();

        var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("Jwt configuration section is missing.");

        if (string.IsNullOrWhiteSpace(jwtOptions.SecretKey) || Encoding.UTF8.GetByteCount(jwtOptions.SecretKey) < 32)
        {
            throw new InvalidOperationException("Jwt:SecretKey must be configured and be at least 256 bits (32 bytes).");
        }

        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwtOptions.Issuer,
                ValidAudience = jwtOptions.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SecretKey)),
                ClockSkew = TimeSpan.Zero
            };

            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (string.IsNullOrEmpty(context.Token) &&
                        context.Request.Cookies.TryGetValue("access_token", out var cookieToken))
                    {
                        context.Token = cookieToken;
                    }
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var validator = context.HttpContext.RequestServices.GetService<JwtTenantValidator>()
                        ?? new JwtTenantValidator();
                    return validator.ValidateAsync(context);
                },
                OnChallenge = context =>
                {
                    var validator = context.HttpContext.RequestServices.GetService<JwtTenantValidator>()
                        ?? new JwtTenantValidator();
                    return validator.HandleChallengeAsync(context);
                }
            };
        });

        services.AddAuthorization();

        return services;
    }
}
