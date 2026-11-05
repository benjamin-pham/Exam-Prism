namespace Tests.Infrastructure;

using Common.MultiTenancy;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

public sealed class TenantServiceScope : IServiceScope, IAsyncDisposable
{
    private readonly IServiceScope _innerScope;
    private readonly IServiceProvider _serviceProvider;
    private readonly ExamPrismTenantInfo _tenantInfo;

    public TenantServiceScope(IServiceScope innerScope, ExamPrismTenantInfo tenantInfo)
    {
        _innerScope = innerScope;
        _tenantInfo = tenantInfo;
        _serviceProvider = new TenantServiceProvider(innerScope.ServiceProvider, this);
        SetContext();
    }

    public void SetContext()
    {
        var setter = _innerScope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>();
        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(_tenantInfo);
    }

    public IServiceProvider ServiceProvider
    {
        get
        {
            SetContext();
            return _serviceProvider;
        }
    }

    public void Dispose()
    {
        _innerScope.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (_innerScope is IAsyncDisposable asyncDisposable)
        {
            return asyncDisposable.DisposeAsync();
        }
        _innerScope.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class TenantServiceProvider(IServiceProvider inner, TenantServiceScope parent) : IServiceProvider, ISupportRequiredService
    {
        public object? GetService(Type serviceType)
        {
            parent.SetContext();
            return inner.GetService(serviceType);
        }

        public object GetRequiredService(Type serviceType)
        {
            parent.SetContext();
            return inner.GetRequiredService(serviceType);
        }
    }
}

public static class MultiTenantTestDatabaseHelper
{
    public static async Task InitializeDatabasesAndSeedAsync(
        PostgreSqlContainer dbContainer,
        IServiceProvider services,
        Func<string, Task<IServiceScope>> createTenantScopeAsync)
    {
        await using (var conn = new NpgsqlConnection(dbContainer.GetConnectionString()))
        {
            await conn.OpenAsync();
            foreach (var dbName in new[] { "examprism_tenant_a", "examprism_tenant_b" })
            {
                await using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = $"SELECT 1 FROM pg_database WHERE datname = '{dbName}'";
                var exists = await checkCmd.ExecuteScalarAsync();
                if (exists is null)
                {
                    await using var createCmd = conn.CreateCommand();
                    createCmd.CommandText = $"CREATE DATABASE {dbName};";
                    await createCmd.ExecuteNonQueryAsync();
                }
            }
        }

        using (var scope = services.CreateScope())
        {
            var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await catalogDb.Database.MigrateAsync();

            var seeder = scope.ServiceProvider.GetRequiredService<PlatformAdminSeeder>();
            await seeder.SeedAsync();

            var protector = scope.ServiceProvider.GetRequiredService<IConnectionStringProtector>();
            var now = DateTime.UtcNow;

            var connStrA = new NpgsqlConnectionStringBuilder(dbContainer.GetConnectionString())
            {
                Database = "examprism_tenant_a"
            }.ConnectionString;

            var connStrB = new NpgsqlConnectionStringBuilder(dbContainer.GetConnectionString())
            {
                Database = "examprism_tenant_b"
            }.ConnectionString;

            if (!await catalogDb.Tenants.AnyAsync(t => t.Slug == "tenant-a"))
            {
                catalogDb.Tenants.AddRange(
                    new Tenant
                    {
                        Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        Slug = "tenant-a",
                        Name = "Tenant A",
                        Status = TenantStatus.Active,
                        EncryptedConnectionString = protector.Protect(connStrA),
                        CreatedAt = now,
                        UpdatedAt = now,
                        ProvisionedAt = now
                    },
                    new Tenant
                    {
                        Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        Slug = "tenant-b",
                        Name = "Tenant B",
                        Status = TenantStatus.Active,
                        EncryptedConnectionString = protector.Protect(connStrB),
                        CreatedAt = now,
                        UpdatedAt = now,
                        ProvisionedAt = now
                    },
                    new Tenant
                    {
                        Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                        Slug = "provisioning-org",
                        Name = "Provisioning Org",
                        Status = TenantStatus.Provisioning,
                        EncryptedConnectionString = null,
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    new Tenant
                    {
                        Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                        Slug = "suspended-org",
                        Name = "Suspended Org",
                        Status = TenantStatus.Suspended,
                        EncryptedConnectionString = protector.Protect(connStrA),
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    new Tenant
                    {
                        Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                        Slug = "failed-org",
                        Name = "Failed Org",
                        Status = TenantStatus.Failed,
                        FailureReason = "Migration timeout",
                        EncryptedConnectionString = null,
                        CreatedAt = now,
                        UpdatedAt = now
                    }
                );
                await catalogDb.SaveChangesAsync();
            }
        }

        using (var scopeA = await createTenantScopeAsync("tenant-a"))
        {
            var appDbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
            await appDbA.Database.MigrateAsync();
        }

        using (var scopeB = await createTenantScopeAsync("tenant-b"))
        {
            var appDbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();
            await appDbB.Database.MigrateAsync();
        }
    }
}
