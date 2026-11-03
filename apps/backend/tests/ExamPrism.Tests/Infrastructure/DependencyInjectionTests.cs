namespace Tests.Infrastructure;

using Common.Caching;
using Common.MultiTenancy;
using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.Caching;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

public class DependencyInjectionTests
{
    private static Dictionary<string, string?> CreateValidSettings() => new()
    {
        { "ConnectionStrings:CatalogConnection", "Host=localhost;Database=test;Username=postgres;Password=postgres" },
        { "Jwt:Issuer", "ExamPrism" },
        { "Jwt:Audience", "ExamPrism" },
        { "Jwt:SecretKey", "SuperSecretKeyForTestingJwtTokensMin256BitsLong!" }
    };

    [Fact]
    public void AddInfrastructure_ThrowsInvalidOperationException_WhenCatalogConnectionStringMissing()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        Action act = () => services.AddInfrastructure(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CatalogConnection*");
    }

    [Fact]
    public void AddInfrastructure_ThrowsInvalidOperationException_WhenJwtSectionMissing()
    {
        var services = new ServiceCollection();
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:CatalogConnection"] = "Host=localhost;Database=test;Username=postgres;Password=postgres"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();

        Action act = () => services.AddInfrastructure(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt configuration section is missing*");
    }

    [Fact]
    public void AddInfrastructure_RegistersMultiTenantAndPersistenceServices()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();

        services.AddInfrastructure(configuration);

        services.Should().ContainSingle(s => s.ServiceType == typeof(CatalogDbContext))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(s => s.ServiceType == typeof(AppDbContext))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(s => s.ServiceType == typeof(ITenantContext))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(s => s.ServiceType == typeof(IConnectionStringProtector))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Should().ContainSingle(s => s.ServiceType == typeof(ITenantConnectionStringNormalizer))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
        services.Should().ContainSingle(s => s.ServiceType == typeof(ITenantCacheInvalidator))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(s => s.ServiceType == typeof(ITenantProvisioner))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(s => s.ServiceType == typeof(ITenantFleetMigrator))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        services.Should().Contain(s => s.ServiceType == typeof(IMultiTenantStore<ExamPrismTenantInfo>))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);

        var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetService<ITenantProvisioner>().Should().NotBeNull();
            scope.ServiceProvider.GetService<ITenantFleetMigrator>().Should().NotBeNull();
        }
        var healthCheckOptions = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        healthCheckOptions.Registrations.Should().Contain(r => r.Name == "catalog_db");
    }

    [Fact]
    public void ResolveAppDbContext_WithoutActiveTenantContext_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();

        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<AppDbContext>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*active tenant context*");
    }

    [Fact]
    public void AddInfrastructure_RegistersTimeProvider_AsSingleton()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();

        services.AddInfrastructure(configuration);

        services.Should().ContainSingle(s => s.ServiceType == typeof(TimeProvider))
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);

        var serviceProvider = services.BuildServiceProvider();
        var timeProvider = serviceProvider.GetService<TimeProvider>();
        timeProvider.Should().NotBeNull();
        timeProvider.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void AddInfrastructure_RegistersIdentityAndTokenService()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();
        var services = new ServiceCollection();

        // Act
        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();

        // Assert
        services.Should().Contain(s => s.ServiceType == typeof(ITokenService));
        services.Should().Contain(s => s.ServiceType == typeof(UserManager<ApplicationUser>));

        using var scope = provider.CreateScope();
        var setter = scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>();
        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(
            new ExamPrismTenantInfo("test-id", "test-slug")
            {
                Status = TenantStatus.Active,
                ConnectionString = "Host=localhost;Database=test;Username=postgres;Password=postgres"
            });

        scope.ServiceProvider.GetService<ITokenService>().Should().NotBeNull();
        scope.ServiceProvider.GetService<UserManager<ApplicationUser>>().Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructure_RegistersUserContextAndHttpContextAccessor()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();
        var services = new ServiceCollection();

        // Act
        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();

        // Assert
        services.Should().ContainSingle(s => s.ServiceType == typeof(Common.Security.IUserContext))
            .Which.Lifetime.Should().Be(ServiceLifetime.Scoped);
        provider.GetService<IHttpContextAccessor>().Should().NotBeNull();
        provider.GetService<Common.Security.IUserContext>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddInfrastructure_ConfiguresJwtBearer_WithCookieFallback()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();
        var services = new ServiceCollection();
        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();

        // Act
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>();
        var jwtOptions = optionsMonitor.Get(JwtBearerDefaults.AuthenticationScheme);

        // Assert TokenValidationParameters
        jwtOptions.TokenValidationParameters.ValidIssuer.Should().Be("ExamPrism");
        jwtOptions.TokenValidationParameters.ValidAudience.Should().Be("ExamPrism");
        jwtOptions.TokenValidationParameters.ValidateLifetime.Should().BeTrue();
        jwtOptions.TokenValidationParameters.ClockSkew.Should().Be(TimeSpan.Zero);

        // Assert OnMessageReceived extracts cookie when token is not set
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Append("Cookie", "access_token=cookie-jwt-token");
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var context = new MessageReceivedContext(httpContext, scheme, jwtOptions);

        await jwtOptions.Events.OnMessageReceived(context);
        context.Token.Should().Be("cookie-jwt-token");

        // Assert OnMessageReceived leaves existing token intact if already set (e.g. from Authorization header)
        var contextWithExisting = new MessageReceivedContext(httpContext, scheme, jwtOptions)
        {
            Token = "existing-bearer-token"
        };
        await jwtOptions.Events.OnMessageReceived(contextWithExisting);
        contextWithExisting.Token.Should().Be("existing-bearer-token");
    }

    [Theory]
    [InlineData("")]
    [InlineData("short-key")]
    public void AddInfrastructure_WithShortOrEmptyJwtSecretKey_ThrowsInvalidOperationException(string secretKey)
    {
        var settings = CreateValidSettings();
        settings["Jwt:SecretKey"] = secretKey;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        var act = () => services.AddInfrastructure(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Jwt:SecretKey*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddInfrastructure_WhenRedisConnectionStringOmittedOrWhitespace_RegistersInMemoryHybridCacheOnly_WithoutRedisOrRedisHealthCheck(string? redisConn)
    {
        var settings = CreateValidSettings();
        if (redisConn is not null)
        {
            settings["ConnectionStrings:Redis"] = redisConn;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();

        provider.GetService<ICacheService>().Should().BeOfType<HybridCacheService>();
        provider.GetService<IConnectionMultiplexer>().Should().BeNull();
        provider.GetService<IDistributedCache>().Should().BeNull();

        var healthCheckOptions = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        healthCheckOptions.Registrations.Should().Contain(r => r.Name == "catalog_db");
        healthCheckOptions.Registrations.Should().NotContain(r => r.Name == "redis");
    }

    [Fact]
    public void AddInfrastructure_WhenRedisConnectionStringProvided_RegistersMultiplexerResilientDistributedCacheAndRedisHealthCheck()
    {
        var settings = CreateValidSettings();
        settings["ConnectionStrings:Redis"] = "localhost:6399"; // Non-running port to prove AbortOnConnectFail=false doesn't crash boot
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();

        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();

        provider.GetService<IConnectionMultiplexer>().Should().NotBeNull();
        provider.GetService<IDistributedCache>().Should().BeOfType<ResilientDistributedCache>();
        provider.GetService<ICacheService>().Should().BeOfType<HybridCacheService>();

        var healthCheckOptions = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        healthCheckOptions.Registrations.Should().Contain(r => r.Name == "catalog_db");
        healthCheckOptions.Registrations.Should().Contain(r => r.Name == "redis");
    }

    [Fact]
    public void AddInfrastructure_RegistersDualIdentity_ResolvingPlatformManagersWithoutActiveTenantContext()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();
        var services = new ServiceCollection();

        // Act
        services.AddInfrastructure(configuration);
        var provider = services.BuildServiceProvider();

        // Assert - in scope without active tenant
        using var scopeWithoutTenant = provider.CreateScope();
        var platformUserManager = scopeWithoutTenant.ServiceProvider.GetService<UserManager<PlatformUser>>();
        platformUserManager.Should().NotBeNull();

        var platformRoleManager = scopeWithoutTenant.ServiceProvider.GetService<RoleManager<PlatformRole>>();
        platformRoleManager.Should().NotBeNull();

        var resolveAppUserAct = () => scopeWithoutTenant.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        resolveAppUserAct.Should().Throw<InvalidOperationException>()
            .WithMessage("*active tenant context*");

        // Assert - in scope with active tenant
        using var scopeWithTenant = provider.CreateScope();
        var setter = scopeWithTenant.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>();
        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(
            new ExamPrismTenantInfo("test-id", "test-slug")
            {
                Status = TenantStatus.Active,
                ConnectionString = "Host=localhost;Database=test;Username=postgres;Password=postgres"
            });

        var appUserManager = scopeWithTenant.ServiceProvider.GetService<UserManager<ApplicationUser>>();
        appUserManager.Should().NotBeNull();
    }
}
