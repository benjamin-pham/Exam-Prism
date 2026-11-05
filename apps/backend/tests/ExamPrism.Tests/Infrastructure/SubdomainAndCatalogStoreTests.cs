namespace Tests.Infrastructure;

using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Testcontainers.PostgreSql;
using Xunit;

public class CatalogDbFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("examprism_catalog_test")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string ConnectionString => Container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();

        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        await using var context = new CatalogDbContext(options);
        await context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}

public class SubdomainAndCatalogStoreTests : IClassFixture<CatalogDbFixture>
{
    private readonly CatalogDbFixture _fixture;

    public SubdomainAndCatalogStoreTests(CatalogDbFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("examprism.com", HostResolutionKind.Platform, null)]
    [InlineData("localhost", HostResolutionKind.Platform, null)]
    [InlineData("127.0.0.1", HostResolutionKind.Platform, null)]
    [InlineData("[::1]", HostResolutionKind.Platform, null)]
    [InlineData("admin.examprism.com", HostResolutionKind.Platform, null)]
    [InlineData("WWW.Staging.ExamPrism.com", HostResolutionKind.Platform, null)]
    [InlineData("tenant-1.localhost", HostResolutionKind.TenantCandidate, "tenant-1")]
    [InlineData("a.b.examprism.com", HostResolutionKind.InvalidTenantHost, null)]
    [InlineData("-bad.examprism.com", HostResolutionKind.InvalidTenantHost, null)]
    public void SubdomainParser_ClassifiesRootReservedValidInvalidAndOverlappingBaseDomains(
        string hostHeader,
        HostResolutionKind expectedKind,
        string? expectedIdentifier)
    {
        var options = new MultiTenancyOptions
        {
            BaseDomains = ["examprism.com", "staging.examprism.com", "localhost"],
            ReservedSubdomains = ["www", "admin", "api"]
        };

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Host = new HostString(hostHeader);

        var result = SubdomainParser.Parse(httpContext, options, isDevelopment: false);

        result.Kind.Should().Be(expectedKind);
        result.Identifier.Should().Be(expectedIdentifier);

        // Verify cached in httpContext.Items
        httpContext.Items.Should().ContainKey(SubdomainParser.HttpContextItemKey);
        httpContext.Items[SubdomainParser.HttpContextItemKey].Should().Be(result);
    }

    [Fact]
    public void SubdomainParser_NormalizesHostWithPortAndTrailingDot_CorrectlyResolvesCandidate()
    {
        var options = new MultiTenancyOptions
        {
            BaseDomains = ["examprism.com", "staging.examprism.com", "localhost"],
            ReservedSubdomains = ["www", "admin", "api"]
        };

        var httpContext = new DefaultHttpContext();
        // Host "SCHOOL-A.Staging.ExamPrism.com.", Port 8443
        httpContext.Request.Host = new HostString("SCHOOL-A.Staging.ExamPrism.com.", 8443);

        var result = SubdomainParser.Parse(httpContext, options, isDevelopment: false);

        result.Kind.Should().Be(HostResolutionKind.TenantCandidate);
        result.Identifier.Should().Be("school-a");
    }

    [Fact]
    public void SubdomainParser_XTenantHeaderFallback_WorksOnlyInDevelopmentOnRootHost_AndNeverOverridesExplicitSubdomain()
    {
        var options = new MultiTenancyOptions
        {
            BaseDomains = ["examprism.com", "staging.examprism.com", "localhost"],
            ReservedSubdomains = ["www", "admin", "api"],
            AllowHeaderFallbackInDevelopment = true,
            HeaderName = "X-Tenant"
        };

        // 1. Host = "localhost", X-Tenant = "School-A", isDevelopment = true -> TenantCandidate("school-a")
        var ctx1 = new DefaultHttpContext();
        ctx1.Request.Host = new HostString("localhost");
        ctx1.Request.Headers["X-Tenant"] = "School-A";
        var res1 = SubdomainParser.Parse(ctx1, options, isDevelopment: true);
        res1.Kind.Should().Be(HostResolutionKind.TenantCandidate);
        res1.Identifier.Should().Be("school-a");

        // 2. Host = "localhost", X-Tenant = "a.b", isDevelopment = true -> InvalidTenantHost
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Host = new HostString("localhost");
        ctx2.Request.Headers["X-Tenant"] = "a.b";
        var res2 = SubdomainParser.Parse(ctx2, options, isDevelopment: true);
        res2.Kind.Should().Be(HostResolutionKind.InvalidTenantHost);
        res2.Identifier.Should().BeNull();

        // 3. Host = "tenant-a.localhost", X-Tenant = "tenant-b", isDevelopment = true -> TenantCandidate("tenant-a") (Review Focus #4)
        var ctx3 = new DefaultHttpContext();
        ctx3.Request.Host = new HostString("tenant-a.localhost");
        ctx3.Request.Headers["X-Tenant"] = "tenant-b";
        var res3 = SubdomainParser.Parse(ctx3, options, isDevelopment: true);
        res3.Kind.Should().Be(HostResolutionKind.TenantCandidate);
        res3.Identifier.Should().Be("tenant-a");

        // 4. Host = "localhost", X-Tenant = "school-a", isDevelopment = false -> Platform (Review Focus #4)
        var ctx4 = new DefaultHttpContext();
        ctx4.Request.Host = new HostString("localhost");
        ctx4.Request.Headers["X-Tenant"] = "school-a";
        var res4 = SubdomainParser.Parse(ctx4, options, isDevelopment: false);
        res4.Kind.Should().Be(HostResolutionKind.Platform);
        res4.Identifier.Should().BeNull();
    }

    [Fact]
    public async Task SubdomainMultiTenantStrategy_ReturnsIdentifierWhenTenantCandidate_AndNullOtherwise()
    {
        var options = Options.Create(new MultiTenancyOptions
        {
            BaseDomains = ["localhost"],
            ReservedSubdomains = ["www"]
        });

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Production);

        var strategy = new SubdomainMultiTenantStrategy(options, env);

        var ctxCandidate = new DefaultHttpContext();
        ctxCandidate.Request.Host = new HostString("school-a.localhost");
        var idCandidate = await strategy.GetIdentifierAsync(ctxCandidate);
        idCandidate.Should().Be("school-a");

        var ctxPlatform = new DefaultHttpContext();
        ctxPlatform.Request.Host = new HostString("localhost");
        var idPlatform = await strategy.GetIdentifierAsync(ctxPlatform);
        idPlatform.Should().BeNull();

        var nonHttpContext = new object();
        var idNonHttp = await strategy.GetIdentifierAsync(nonHttpContext);
        idNonHttp.Should().BeNull();
    }

    [Fact]
    public async Task CatalogMultiTenantStore_CachesCiphertextOnlyInDistributedCache_DecryptsOnReadForActiveTenant_AndWithholdsConnectionStringWhenNonActive()
    {
        var recordingCache = new RecordingDistributedCache();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache>(recordingCache);
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        var hybridCache = sp.GetRequiredService<HybridCache>();

        var protector = Substitute.For<IConnectionStringProtector>();
        const string plaintext = "Host=pg;Database=t1;Password=SuperSecretPgPass!";
        const string ciphertext = "ENC:CIPHERTEXT_12345";
        protector.Protect(plaintext).Returns(ciphertext);
        protector.Unprotect(ciphertext).Returns(plaintext);

        var dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using var db = new CatalogDbContext(dbOptions);

        var activeId = Guid.NewGuid();
        var suspendedId = Guid.NewGuid();

        var activeTenant = new Tenant
        {
            Id = activeId,
            Slug = "active-school",
            Name = "Active School",
            Status = TenantStatus.Active,
            EncryptedConnectionString = ciphertext,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var suspendedTenant = new Tenant
        {
            Id = suspendedId,
            Slug = "suspended-school",
            Name = "Suspended School",
            Status = TenantStatus.Suspended,
            EncryptedConnectionString = ciphertext,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Tenants.AddRange(activeTenant, suspendedTenant);
        await db.SaveChangesAsync();

        var mtOptions = Options.Create(new MultiTenancyOptions
        {
            CatalogCacheExpirationMinutes = 30,
            CatalogLocalCacheExpirationMinutes = 5,
            NotFoundCacheExpirationSeconds = 30
        });

        var store = new CatalogMultiTenantStore(db, hybridCache, protector, mtOptions);

        // 1. Resolve Active Tenant
        var activeResolved = await store.GetByIdentifierAsync("active-school");
        activeResolved.Should().NotBeNull();
        activeResolved!.Id.Should().Be(activeId.ToString("D"));
        activeResolved.Identifier.Should().Be("active-school");
        activeResolved.TenantGuid.Should().Be(activeId);
        activeResolved.Status.Should().Be(TenantStatus.Active);
        activeResolved.ConnectionString.Should().Be(plaintext);
        activeResolved.ConnectionString.Should().Contain("SuperSecretPgPass!");

        // Review Focus #2: Check raw bytes inside IDistributedCache
        recordingCache.Entries.Should().NotBeEmpty();
        foreach (var entry in recordingCache.Entries.Values)
        {
            var text = Encoding.UTF8.GetString(entry);
            text.Should().NotContain("SuperSecretPgPass!");
        }

        // 2. Resolve Suspended Tenant
        var suspendedResolved = await store.GetByIdentifierAsync("suspended-school");
        suspendedResolved.Should().NotBeNull();
        suspendedResolved!.Id.Should().Be(suspendedId.ToString("D"));
        suspendedResolved.Identifier.Should().Be("suspended-school");
        suspendedResolved.TenantGuid.Should().Be(suspendedId);
        suspendedResolved.Status.Should().Be(TenantStatus.Suspended);
        suspendedResolved.ConnectionString.Should().BeNull();
    }

    [Fact]
    public async Task CatalogMultiTenantStore_NegativeCachesUnknownSlug_AndInvalidatesOnAddOrUpdate()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        var hybridCache = sp.GetRequiredService<HybridCache>();

        var protector = Substitute.For<IConnectionStringProtector>();
        protector.Protect(Arg.Any<string>()).Returns(callInfo => "ENC:" + callInfo.Arg<string>());
        protector.Unprotect(Arg.Any<string>()).Returns(callInfo => callInfo.Arg<string>().Replace("ENC:", ""));

        var interceptor = new QueryCountingInterceptor();
        var dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new CatalogDbContext(dbOptions);

        var mtOptions = Options.Create(new MultiTenancyOptions
        {
            CatalogCacheExpirationMinutes = 30,
            CatalogLocalCacheExpirationMinutes = 5,
            NotFoundCacheExpirationSeconds = 30
        });

        var store = new CatalogMultiTenantStore(db, hybridCache, protector, mtOptions);

        const string missingSlug = "missing-org";

        // Query missing slug first time (DB query 1)
        var miss1 = await store.GetByIdentifierAsync(missingSlug);
        miss1.Should().BeNull();
        var queriesAfterFirstMiss = interceptor.QueryCount;
        queriesAfterFirstMiss.Should().BeGreaterThan(0);

        // Query missing slug second time (Cached, DB query count remains same)
        var miss2 = await store.GetByIdentifierAsync(missingSlug);
        miss2.Should().BeNull();
        interceptor.QueryCount.Should().Be(queriesAfterFirstMiss);

        // Now Add tenant for missing-org
        var newTenantId = Guid.NewGuid();
        var addResult = await store.AddAsync(new ExamPrismTenantInfo(newTenantId.ToString("D"), missingSlug)
        {
            TenantGuid = newTenantId,
            Name = "Newly Added Org",
            Status = TenantStatus.Active,
            ConnectionString = "Host=pg;Database=missing_org;Password=secret"
        });
        addResult.Should().BeTrue();

        // Query missing-org again: Cache should be invalidated, returns newly added tenant
        var found = await store.GetByIdentifierAsync(missingSlug);
        found.Should().NotBeNull();
        found!.Id.Should().Be(newTenantId.ToString("D"));
        found.Identifier.Should().Be(missingSlug);
        found.Name.Should().Be("Newly Added Org");
        found.Status.Should().Be(TenantStatus.Active);
        found.ConnectionString.Should().Be("Host=pg;Database=missing_org;Password=secret");
        interceptor.QueryCount.Should().BeGreaterThan(queriesAfterFirstMiss);
    }

    [Fact]
    public async Task CatalogMultiTenantStore_GetAsyncById_ResolvesAndCaches_AndInvalidatesOnUpdateOrRemove()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        var hybridCache = sp.GetRequiredService<HybridCache>();

        var protector = Substitute.For<IConnectionStringProtector>();
        protector.Protect(Arg.Any<string>()).Returns(ci => "ENC:" + ci.Arg<string>());
        protector.Unprotect(Arg.Any<string>()).Returns(ci => ci.Arg<string>().Replace("ENC:", ""));

        var dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using var db = new CatalogDbContext(dbOptions);

        var tenantId = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = tenantId,
            Slug = "id-lookup-tenant",
            Name = "ID Lookup Tenant",
            Status = TenantStatus.Active,
            EncryptedConnectionString = "ENC:Host=pg;Database=id_test",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var mtOptions = Options.Create(new MultiTenancyOptions());
        var store = new CatalogMultiTenantStore(db, hybridCache, protector, mtOptions);

        // 1. Get by ID
        var resolved = await store.GetAsync(tenantId.ToString("D"));
        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(tenantId.ToString("D"));
        resolved.Identifier.Should().Be("id-lookup-tenant");
        resolved.Status.Should().Be(TenantStatus.Active);
        resolved.ConnectionString.Should().Be("Host=pg;Database=id_test");

        // 2. Non-existent ID returns null
        var missing = await store.GetAsync(Guid.NewGuid().ToString("D"));
        missing.Should().BeNull();

        // 3. Invalid ID format returns null
        var invalid = await store.GetAsync("not-a-guid");
        invalid.Should().BeNull();

        // 4. Update tenant name
        var updatedTenantInfo = new ExamPrismTenantInfo(resolved.Id!, resolved.Identifier!)
        {
            TenantGuid = resolved.TenantGuid,
            Name = "Updated Name",
            Status = resolved.Status,
            ConnectionString = resolved.ConnectionString
        };
        var updateResult = await store.UpdateAsync(updatedTenantInfo);
        updateResult.Should().BeTrue();

        var updated = await store.GetAsync(tenantId.ToString("D"));
        updated.Should().NotBeNull();
        updated!.Name.Should().Be("Updated Name");

        // 5. Remove tenant
        var removeResult = await store.RemoveAsync("id-lookup-tenant");
        removeResult.Should().BeTrue();

        var afterRemove = await store.GetByIdentifierAsync("id-lookup-tenant");
        afterRemove.Should().BeNull();
    }

    [Fact]
    public async Task CatalogMultiTenantStore_GetAllAsync_ReturnsAllMappedTenants()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        var hybridCache = sp.GetRequiredService<HybridCache>();

        var protector = Substitute.For<IConnectionStringProtector>();
        protector.Unprotect(Arg.Any<string>()).Returns(ci => ci.Arg<string>());

        var dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using var db = new CatalogDbContext(dbOptions);

        var mtOptions = Options.Create(new MultiTenancyOptions());
        var store = new CatalogMultiTenantStore(db, hybridCache, protector, mtOptions);

        var all = (await store.GetAllAsync()).ToList();
        all.Should().NotBeNull();

        var paged = (await store.GetAllAsync(take: 2, skip: 0)).ToList();
        paged.Count.Should().BeInRange(0, 2);
    }

    private sealed class RecordingDistributedCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
        public ConcurrentDictionary<string, byte[]> Entries { get; } = new();

        public byte[]? Get(string key) => _inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Entries[key] = value;
            _inner.Set(key, value, options);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Entries[key] = value;
            return _inner.SetAsync(key, value, options, token);
        }

        public void Refresh(string key) => _inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);

        public void Remove(string key)
        {
            Entries.TryRemove(key, out _);
            _inner.Remove(key);
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Entries.TryRemove(key, out _);
            return _inner.RemoveAsync(key, token);
        }
    }

    private sealed class QueryCountingInterceptor : DbCommandInterceptor
    {
        public int QueryCount { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            QueryCount++;
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            QueryCount++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
