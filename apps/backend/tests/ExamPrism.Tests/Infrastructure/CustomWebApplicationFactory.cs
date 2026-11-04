namespace Tests.Infrastructure;

using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Xunit;

public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("examprism_catalog_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public CustomWebApplicationFactory()
    {
        ClientOptions.BaseAddress = new Uri("http://tenant-a.localhost");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:CatalogConnection", _dbContainer.GetConnectionString());
        builder.UseSetting("MultiTenancy:BaseDomains:0", "localhost");
        builder.UseSetting("RateLimiting:Global:PermitLimit", "10000");
        builder.UseSetting("RateLimiting:Auth:PermitLimit", "10000");
        builder.UseSetting("PlatformAdmin:SeedOnStartup", "false");
        builder.UseSetting("PlatformAdmin:Email", "admin@examprism.com");
        builder.UseSetting("PlatformAdmin:Password", "AdminPassword123!");
        builder.UseSetting("MultiTenancy:Provisioning:RecoverStuckProvisioningOnStartup", "false");
        builder.UseSetting("MultiTenancy:FleetMigration:MigrateOnStartup", "false");
        builder.UseSetting("Orleans:SiloPort", "0");
        builder.UseSetting("Orleans:GatewayPort", "0");
        builder.UseSetting("Persistence:MaxRetryCount", "0");
    }

    public async Task InitializeAsync()
    {
        await _dbContainer.StartAsync();
        await MultiTenantTestDatabaseHelper.InitializeDatabasesAndSeedAsync(
            _dbContainer,
            Services,
            CreateTenantScopeAsync);
    }

    public HttpClient CreateTenantClient(string slug)
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"http://{slug}.localhost")
        });
    }

    public HttpClient CreatePlatformClient()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost")
        });
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
            await _dbContainer.DisposeAsync();
        }
    }
}
