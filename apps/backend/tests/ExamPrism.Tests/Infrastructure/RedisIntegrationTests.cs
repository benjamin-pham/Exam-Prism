namespace Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using Common.Caching;
using ExamPrism.Features.Auth;
using ExamPrism.Features.Health;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;
using Xunit;

public class RedisWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("examprism_redis_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    private readonly RedisContainer _redisContainer = new RedisBuilder("redis:7-alpine")
        .Build();

    public RedisContainer RedisContainer => _redisContainer;

    public RedisWebApplicationFactory()
    {
        ClientOptions.BaseAddress = new Uri("http://tenant-a.localhost");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:CatalogConnection", _dbContainer.GetConnectionString());
        builder.UseSetting("ConnectionStrings:Redis", _redisContainer.GetConnectionString());
        builder.UseSetting("MultiTenancy:BaseDomains:0", "localhost");
        builder.UseSetting("RateLimiting:Auth:PermitLimit", "2");
        builder.UseSetting("RateLimiting:Auth:WindowSeconds", "60");
        builder.UseSetting("RateLimiting:Global:PermitLimit", "3");
        builder.UseSetting("RateLimiting:Global:WindowSeconds", "60");
        builder.UseSetting("Orleans:SiloPort", "0");
        builder.UseSetting("Orleans:GatewayPort", "0");
        builder.UseSetting("Jwt:Issuer", "ExamPrism");
        builder.UseSetting("Jwt:Audience", "ExamPrism");
        builder.UseSetting("Jwt:SecretKey", "SuperSecretKeyForDevJwtTokensMin256BitsLongExamPrismDevOnly!");
        builder.UseSetting("PlatformAdmin:SeedOnStartup", "false");
        builder.UseSetting("MultiTenancy:Provisioning:RecoverStuckProvisioningOnStartup", "false");
        builder.UseSetting("MultiTenancy:FleetMigration:MigrateOnStartup", "false");
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_dbContainer.StartAsync(), _redisContainer.StartAsync());
        await MultiTenantTestDatabaseHelper.InitializeDatabasesAndSeedAsync(
            _dbContainer,
            Services,
            CreateTenantScopeAsync);
    }

    public async Task<IServiceScope> CreateTenantScopeAsync(string slug = "tenant-a")
    {
        var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IMultiTenantStore<ExamPrismTenantInfo>>();
        var tenantInfo = await store.GetByIdentifierAsync(slug)
            ?? throw new InvalidOperationException($"Tenant '{slug}' not found in store.");

        return new TenantServiceScope(scope, tenantInfo);
    }

    public new async Task DisposeAsync()
    {
        try
        {
            await base.DisposeAsync();
        }
        finally
        {
            await Task.WhenAll(_dbContainer.DisposeAsync().AsTask(), _redisContainer.DisposeAsync().AsTask());
        }
    }
}

public record QuizCacheProbe(string Subject, int Score);

public class RedisIntegrationTests : IClassFixture<RedisWebApplicationFactory>
{
    private readonly RedisWebApplicationFactory _factory;

    public RedisIntegrationTests(RedisWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CacheService_WithRedisConfigured_PersistsEntriesToRedisL2WithConfiguredKeyPrefix()
    {
        var cache = _factory.Services.GetRequiredService<ICacheService>();
        var multiplexer = _factory.Services.GetRequiredService<IConnectionMultiplexer>();

        var cacheKey = $"quiz:{Guid.NewGuid()}";
        var probe = new QuizCacheProbe("Math", 50);

        await cache.SetAsync(cacheKey, probe);

        var keyExistsInRedis = await multiplexer.GetDatabase().KeyExistsAsync($"examprism:platform:{cacheKey}");
        keyExistsInRedis.Should().BeTrue();

        var retrieved = await cache.GetOrDefaultAsync<QuizCacheProbe>(cacheKey);
        retrieved.Should().NotBeNull();
        retrieved!.Subject.Should().Be("Math");
        retrieved.Score.Should().Be(50);
    }

    [Fact]
    public async Task RateLimiting_WithRedisConfigured_StoresCountersInRedis_AndReturns429WhenPermitLimitExceeded()
    {
        var client = _factory.CreateClient();
        var multiplexer = _factory.Services.GetRequiredService<IConnectionMultiplexer>();

        var cmd = new LoginCommand("redis-limit@example.com", "WrongPassword!");

        // 1st request -> 401
        var res1 = await client.PostAsJsonAsync("/api/auth/login", cmd);
        res1.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 2nd request -> 401
        var res2 = await client.PostAsJsonAsync("/api/auth/login", cmd);
        res2.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 3rd request -> 429
        var res3 = await client.PostAsJsonAsync("/api/auth/login", cmd);
        res3.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        res3.Headers.TryGetValues("Retry-After", out var retryAfter).Should().BeTrue();
        int.Parse(retryAfter!.First()).Should().BeGreaterThanOrEqualTo(1);

        // Verify Redis rate limit key was created
        var endpoint = multiplexer.GetEndPoints()[0];
        var server = multiplexer.GetServer(endpoint);
        var keys = server.Keys(pattern: "examprism:ratelimit:*:auth:*").ToList();
        keys.Should().NotBeEmpty();
    }

    [Fact]
    public async Task HealthEndpoint_WithRedisConfigured_Returns200HealthyWhenRedisUp()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadFromJsonAsync<GetHealthResult>();
        content.Should().NotBeNull();
        content!.Status.Should().Be("Healthy");
    }

    [Fact]
    public async Task UnreachableRedis_ReportsUnhealthy503OnHealthEndpoint_WhileApiEndpointsFailOpenWithout500()
    {
        var brokenFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Redis", "127.0.0.1:6399,connectTimeout=200,syncTimeout=200");
        });

        var brokenClient = brokenFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://tenant-a.localhost")
        });

        // 1. Health check returns 503 Unhealthy because Redis is down
        var healthRes = await brokenClient.GetAsync("/health");
        healthRes.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var healthContent = await healthRes.Content.ReadFromJsonAsync<GetHealthResult>();
        healthContent.Should().NotBeNull();
        healthContent!.Status.Should().Be("Unhealthy");

        // 2. Auth endpoint fails open through Redis rate limiter without crashing or returning 500
        var cmd = new LoginCommand("nobody@example.com", "Password123!");
        var loginRes = await brokenClient.PostAsJsonAsync("/api/auth/login", cmd);
        loginRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 3. CacheService fails open gracefully: GetOrCreateAsync returns computed value without throwing
        var cacheService = brokenFactory.Services.GetRequiredService<ICacheService>();
        var value = await cacheService.GetOrCreateAsync(
            $"unreachable:{Guid.NewGuid()}",
            _ => ValueTask.FromResult("fallback-value"));
        value.Should().Be("fallback-value");
    }
}
