namespace ExamPrism.Tests.Infrastructure;

using System.Data;
using System.Data.Common;
using Common.Caching;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.Http;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

public class AntiDdosCacheAndOutputCacheTests
{
    private sealed class QueryCountingInterceptor : DbCommandInterceptor
    {
        private int _executedSelectCount;
        private readonly bool _returnExistingTenant;
        private readonly Guid _existingTenantId = Guid.NewGuid();
        private readonly string _existingSlug = "active-tenant";

        public QueryCountingInterceptor(bool returnExistingTenant = false)
        {
            _returnExistingTenant = returnExistingTenant;
        }

        public int ExecutedSelectCount => _executedSelectCount;
        public Guid ExistingTenantId => _existingTenantId;
        public string ExistingSlug => _existingSlug;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _executedSelectCount);
            }

            var table = new DataTable();
            table.Columns.Add("Id", typeof(Guid));
            table.Columns.Add("CreatedAt", typeof(DateTime));
            table.Columns.Add("EncryptedConnectionString", typeof(string));
            table.Columns.Add("EncryptedOwnerPassword", typeof(string));
            table.Columns.Add("FailureReason", typeof(string));
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("OwnerEmail", typeof(string));
            table.Columns.Add("ProvisionedAt", typeof(DateTime));
            table.Columns.Add("Slug", typeof(string));
            table.Columns.Add("Status", typeof(string));
            table.Columns.Add("UpdatedAt", typeof(DateTime));

            if (_returnExistingTenant)
            {
                table.Rows.Add(
                    _existingTenantId,
                    DateTime.UtcNow,
                    "ENC:conn",
                    null,
                    null,
                    "Active Tenant Name",
                    "owner@example.com",
                    DateTime.UtcNow,
                    _existingSlug,
                    TenantStatus.Active.ToString(),
                    DateTime.UtcNow);
            }

            return new ValueTask<InterceptionResult<DbDataReader>>(
                InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
    }

    private sealed class SuppressConnectionInterceptor : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<InterceptionResult>(InterceptionResult.Suppress());
        }
    }

    [Fact]
    public async Task CatalogMultiTenantStore_CoalescesConcurrentColdSlugLookups_IntoSingleDatabaseQuery()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        var hybridCache = sp.GetRequiredService<HybridCache>();

        var protector = Substitute.For<IConnectionStringProtector>();
        var interceptor = new QueryCountingInterceptor(returnExistingTenant: false);
        var connInterceptor = new SuppressConnectionInterceptor();

        var dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=dummy_test;Username=dummy;Password=dummy")
            .AddInterceptors(connInterceptor, interceptor)
            .Options;

        await using var db = new CatalogDbContext(dbOptions);

        var mtOptions = Options.Create(new MultiTenancyOptions
        {
            CatalogCacheExpirationMinutes = 30,
            CatalogLocalCacheExpirationMinutes = 5,
            NotFoundCacheExpirationSeconds = 30
        });

        var store = new CatalogMultiTenantStore(db, hybridCache, protector, mtOptions);

        const string slug = "cold-slug";

        var tasks = Enumerable.Range(0, 40)
            .Select(_ => Task.Run(async () => await store.GetByIdentifierAsync(slug)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r => r.Should().BeNull());
        interceptor.ExecutedSelectCount.Should().Be(1);
    }

    [Fact]
    public async Task CatalogMultiTenantStore_CoalescesConcurrentColdSlugLookups_ForExistingTenant_IntoSingleDatabaseQuery()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        var hybridCache = sp.GetRequiredService<HybridCache>();

        var protector = Substitute.For<IConnectionStringProtector>();
        protector.Unprotect("ENC:conn").Returns("Host=tenantdb;Database=active");

        var interceptor = new QueryCountingInterceptor(returnExistingTenant: true);
        var connInterceptor = new SuppressConnectionInterceptor();

        var dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=dummy_test;Username=dummy;Password=dummy")
            .AddInterceptors(connInterceptor, interceptor)
            .Options;

        await using var db = new CatalogDbContext(dbOptions);

        var mtOptions = Options.Create(new MultiTenancyOptions
        {
            CatalogCacheExpirationMinutes = 30,
            CatalogLocalCacheExpirationMinutes = 5,
            NotFoundCacheExpirationSeconds = 30
        });

        var store = new CatalogMultiTenantStore(db, hybridCache, protector, mtOptions);

        var tasks = Enumerable.Range(0, 40)
            .Select(_ => Task.Run(async () => await store.GetByIdentifierAsync(interceptor.ExistingSlug)))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r =>
        {
            r.Should().NotBeNull();
            r!.Identifier.Should().Be(interceptor.ExistingSlug);
            r.TenantGuid.Should().Be(interceptor.ExistingTenantId);
            r.Status.Should().Be(TenantStatus.Active);
            r.ConnectionString.Should().Be("Host=tenantdb;Database=active");
        });

        interceptor.ExecutedSelectCount.Should().Be(1);
    }

    [Fact]
    public async Task RedisOutputCacheStore_StoresGetsAndEvictsByTagViaRedis_AndFailsOpenToFallbackOnRedisError()
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        multiplexer.IsConnected.Returns(true);
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var fallbackStore = Substitute.For<IOutputCacheStore>();
        var store = new RedisOutputCacheStore(multiplexer, fallbackStore);

        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var validFor = TimeSpan.FromMinutes(10);
        var tags = new[] { "tag-exam", "tag-tenant-1" };

        // 1. SetAsync stores payload in Redis and adds key to tag sets
        await store.SetAsync("cache-key-1", payload, tags, validFor, CancellationToken.None);

        await database.Received(1).StringSetAsync(
            Arg.Is<RedisKey>(k => k == "cache-key-1"),
            Arg.Any<RedisValue>(),
            Arg.Is<Expiration>(e => e.Equals((Expiration)validFor)),
            Arg.Any<ValueCondition>(),
            Arg.Any<CommandFlags>());

        await database.Received(1).SetAddAsync(
            Arg.Is<RedisKey>(k => k == "outputcache:tag:tag-exam"),
            Arg.Is<RedisValue>(v => v == "cache-key-1"),
            Arg.Any<CommandFlags>());

        await database.Received(1).SetAddAsync(
            Arg.Is<RedisKey>(k => k == "outputcache:tag:tag-tenant-1"),
            Arg.Is<RedisValue>(v => v == "cache-key-1"),
            Arg.Any<CommandFlags>());

        // 2. GetAsync returns stored payload from Redis
        database.StringGetAsync(Arg.Is<RedisKey>(k => k == "cache-key-1"), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult((RedisValue)payload));

        var retrieved = await store.GetAsync("cache-key-1", CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved.Should().Equal(payload);

        // 3. EvictByTagAsync executes Lua script to evict keys belonging to tag
        await store.EvictByTagAsync("tag-exam", CancellationToken.None);

        await database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == "outputcache:tag:tag-exam"),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>());

        // 4. Redis error during GetAsync / SetAsync fails open to fallback store without throwing
        database.When(d => d.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<Expiration>(),
                Arg.Any<ValueCondition>(),
                Arg.Any<CommandFlags>()))
            .Do(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated Redis failure"));

        database.When(d => d.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()))
            .Do(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Simulated Redis failure"));

        fallbackStore.GetAsync("failing-key", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<byte[]?>(payload));

        Func<Task> actSet = async () => await store.SetAsync("failing-key", payload, null, validFor, CancellationToken.None);
        await actSet.Should().NotThrowAsync();
        await fallbackStore.Received(1).SetAsync("failing-key", payload, null, validFor, Arg.Any<CancellationToken>());

        Func<Task<byte[]?>> actGet = async () => await store.GetAsync("failing-key", CancellationToken.None);
        var fallbackGetResult = await actGet.Should().NotThrowAsync();
        fallbackGetResult.Subject.Should().Equal(payload);
        await fallbackStore.Received(1).GetAsync("failing-key", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RedisOutputCacheStore_OperatesInDualMode_WithFallbackWhenRedisNotConfigured()
    {
        // When Redis multiplexer is null (e.g. Redis disabled in local/testing environment),
        // store operates seamlessly via internal fallback store.
        var store = new RedisOutputCacheStore(connectionMultiplexer: null);

        var payload = new byte[] { 42, 43, 44 };
        var tags = new[] { "dual-mode-tag" };

        await store.SetAsync("dm-key-1", payload, tags, TimeSpan.FromMinutes(5), CancellationToken.None);

        var retrieved = await store.GetAsync("dm-key-1", CancellationToken.None);
        retrieved.Should().NotBeNull();
        retrieved.Should().Equal(payload);

        // Evict by tag removes it from fallback store
        await store.EvictByTagAsync("dual-mode-tag", CancellationToken.None);

        var afterEviction = await store.GetAsync("dm-key-1", CancellationToken.None);
        afterEviction.Should().BeNull();
    }
}
