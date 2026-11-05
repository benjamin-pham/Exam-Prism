namespace Tests.Infrastructure;

using System.Linq.Expressions;
using Common.Domain;
using Common.Domain.Specifications;
using Common.MultiTenancy;
using Common.Persistence;
using Dapper;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using ExamPrism.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Wolverine;
using Xunit;

public class DddPersistenceIntegrationTests : IClassFixture<CatalogDbFixture>
{
    private readonly CatalogDbFixture _fixture;

    public DddPersistenceIntegrationTests(CatalogDbFixture fixture)
    {
        _fixture = fixture;
    }

    private CatalogDbContext CreateCatalogDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        return new CatalogDbContext(options);
    }

    private sealed class CustomCatalogTenantCountQuery(string prefix) : IDbQuery<CatalogDbContext, int>
    {
        public Task<int> ExecuteAsync(CatalogDbContext db, CancellationToken ct = default)
        {
            return db.Tenants.CountAsync(t => t.Slug.StartsWith(prefix), ct);
        }
    }

    public record TenantSummaryDto(Guid Id, string Slug, string Name, TenantStatus Status);

    private sealed class TenantByPrefixSpec : Specification<Tenant>
    {
        public TenantByPrefixSpec(string prefix)
        {
            Where(t => t.Slug.StartsWith(prefix));
            ApplyOrderBy(t => t.Slug);
        }
    }

    private sealed class PagedTenantSpec : Specification<Tenant>
    {
        public PagedTenantSpec(string prefix, int skip, int take)
        {
            Where(t => t.Slug.StartsWith(prefix));
            ApplyOrderBy(t => t.Slug);
            ApplyPaging(skip, take);
        }
    }

    [Fact]
    public async Task WriteSideDbContext_ExecutesIDbQuery_AndEntitySpecifications()
    {
        await using var db = CreateCatalogDbContext();
        var prefix = "write-query-" + Guid.NewGuid().ToString("N")[..6];

        var t1 = new Tenant { Id = Guid.NewGuid(), Slug = $"{prefix}-1", Name = "Tenant One", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var t2 = new Tenant { Id = Guid.NewGuid(), Slug = $"{prefix}-2", Name = "Tenant Two", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var t3 = new Tenant { Id = Guid.NewGuid(), Slug = $"{prefix}-3", Name = "Tenant Three", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

        db.Tenants.AddRange(t1, t2, t3);
        await db.SaveChangesAsync();

        var count = await db.QueryAsync(new CustomCatalogTenantCountQuery(prefix));
        count.Should().Be(3);

        var firstSpec = new TenantByPrefixSpec(prefix);
        var firstEntity = await db.QueryFirstOrDefaultAsync(firstSpec);
        firstEntity.Should().NotBeNull();
        firstEntity!.Slug.Should().Be($"{prefix}-1");

        var listEntities = await db.QueryListAsync(firstSpec);
        listEntities.Should().HaveCount(3);

        var pagedSpec = new PagedTenantSpec(prefix, skip: 0, take: 1);
        var pagedList = await db.QueryListAsync(pagedSpec);
        pagedList.Should().HaveCount(1);

        var exists = await db.QueryAnyAsync(pagedSpec);
        exists.Should().BeTrue();

        var totalCount = await db.QueryCountAsync(pagedSpec);
        totalCount.Should().Be(3);
    }

    [Fact]
    public async Task CatalogDbContext_ExecutesNativeDapper_OnDbConnection_MapStringEnums_AndSupportsMultipleQueries()
    {
        await using var db = CreateCatalogDbContext();
        var prefix = "dapper-test-" + Guid.NewGuid().ToString("N")[..6];

        var t1 = new Tenant { Id = Guid.NewGuid(), Slug = $"{prefix}-active", Name = "Active Org", Status = TenantStatus.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var t2 = new Tenant { Id = Guid.NewGuid(), Slug = $"{prefix}-suspended", Name = "Suspended Org", Status = TenantStatus.Suspended, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };

        db.Tenants.AddRange(t1, t2);
        await db.SaveChangesAsync();

        var connection = db.Database.GetDbConnection();

        // 1. QueryAsync
        var list = (await connection.QueryAsync<TenantSummaryDto>(
            new CommandDefinition(
                "SELECT \"Id\", \"Slug\", \"Name\", \"Status\" FROM \"Tenants\" WHERE \"Slug\" LIKE @Pattern ORDER BY \"Slug\"",
                new { Pattern = $"{prefix}%" }))).AsList();
        list.Should().HaveCount(2);
        list[0].Status.Should().Be(TenantStatus.Active);
        list[1].Status.Should().Be(TenantStatus.Suspended);

        // 2. QueryFirstOrDefaultAsync
        var found = await connection.QueryFirstOrDefaultAsync<TenantSummaryDto>(
            new CommandDefinition(
                "SELECT \"Id\", \"Slug\", \"Name\", \"Status\" FROM \"Tenants\" WHERE \"Slug\" = @Slug",
                new { Slug = $"{prefix}-active" }));
        found.Should().NotBeNull();
        found!.Name.Should().Be("Active Org");
        found.Status.Should().Be(TenantStatus.Active);

        // 3. QuerySingleOrDefaultAsync
        var single = await connection.QuerySingleOrDefaultAsync<TenantSummaryDto>(
            new CommandDefinition(
                "SELECT \"Id\", \"Slug\", \"Name\", \"Status\" FROM \"Tenants\" WHERE \"Slug\" = @Slug",
                new { Slug = $"{prefix}-suspended" }));
        single.Should().NotBeNull();
        single!.Status.Should().Be(TenantStatus.Suspended);

        // 4. QuerySingleAsync
        var count = await connection.QuerySingleAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM \"Tenants\" WHERE \"Slug\" LIKE @Pattern",
                new { Pattern = $"{prefix}%" }));
        count.Should().Be(2);

        // 5. ExecuteScalarAsync
        var exists = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM \"Tenants\" WHERE \"Slug\" = @Slug)",
                new { Slug = $"{prefix}-active" }));
        exists.Should().BeTrue();

        // 6. QueryMultipleAsync
        await using var grid = await connection.QueryMultipleAsync(
            new CommandDefinition(
                $"""
                SELECT COUNT(*) FROM "Tenants" WHERE "Slug" LIKE @Pattern;
                SELECT "Id", "Slug", "Name", "Status" FROM "Tenants" WHERE "Slug" LIKE @Pattern ORDER BY "Slug";
                """,
                new { Pattern = $"{prefix}%" }));

        var total = await grid.ReadSingleAsync<int>();
        var items = (await grid.ReadAsync<TenantSummaryDto>()).AsList();

        total.Should().Be(2);
        items.Should().HaveCount(2);
    }

    public sealed record SampleAuditedEvent(string Message) : DomainEventBase;

    public sealed class TestAuditableItem : AggregateRoot<Guid>, IAuditableEntity
    {
        public string Title { get; set; } = string.Empty;
        public string UniqueKey { get; set; } = string.Empty;
        public DateTime CreatedAt { get; private set; }
        public DateTime UpdatedAt { get; private set; }

        public TestAuditableItem() { }
        public TestAuditableItem(Guid id, string title, string uniqueKey) : base(id)
        {
            Title = title;
            UniqueKey = uniqueKey;
        }

        public void TriggerEvent(string msg) => RaiseDomainEvent(new SampleAuditedEvent(msg));
        public void SetCreated(DateTime now) => CreatedAt = now;
        public void SetUpdated(DateTime now) => UpdatedAt = now;
    }

    public sealed class TestDddDbContext(
        DbContextOptions<TestDddDbContext> options,
        AuditableEntityInterceptor auditableInterceptor,
        DomainEventDispatchInterceptor dispatchInterceptor) : DbContext(options)
    {
        public DbSet<TestAuditableItem> Items => Set<TestAuditableItem>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.AddInterceptors(auditableInterceptor, dispatchInterceptor);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestAuditableItem>(b =>
            {
                b.ToTable("TestAuditableItems");
                b.HasKey(x => x.Id);
                b.HasIndex(x => x.UniqueKey).IsUnique();
                b.Ignore(x => x.DomainEvents);
            });
        }
    }

    [Fact]
    public async Task AuditableEntityInterceptor_AutomaticallyStampsCreatedAtAndUpdatedAt_WhenNotExplicitlySet()
    {
        var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        var timeProvider = new FakeTimeProvider(now);
        var auditableInterceptor = new AuditableEntityInterceptor(timeProvider);
        var dispatchInterceptor = new DomainEventDispatchInterceptor(new ServiceCollection().BuildServiceProvider());

        var options = new DbContextOptionsBuilder<TestDddDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using var db = new TestDddDbContext(options, auditableInterceptor, dispatchInterceptor);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TestAuditableItems" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Title" text NOT NULL,
                "UniqueKey" text NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_TestAuditableItems_UniqueKey" ON "TestAuditableItems" ("UniqueKey");
        """);

        var item = new TestAuditableItem(Guid.NewGuid(), "Original", Guid.NewGuid().ToString("N"));
        db.Items.Add(item);
        await db.SaveChangesAsync();

        item.CreatedAt.Should().Be(now);
        item.UpdatedAt.Should().Be(now);

        // Advance time and update
        var updatedNow = now.AddHours(2);
        timeProvider.SetUtcNow(updatedNow);

        item.Title = "Modified";
        await db.SaveChangesAsync();

        item.CreatedAt.Should().Be(now);
        item.UpdatedAt.Should().Be(updatedNow);
    }

    [Fact]
    public async Task DomainEventDispatchInterceptor_PublishesEventsAfterSaveChanges_AndDiscardsEventsOnSaveFailure()
    {
        var timeProvider = new FakeTimeProvider();
        var auditableInterceptor = new AuditableEntityInterceptor(timeProvider);
        var bus = Substitute.For<IMessageBus>();

        var services = new ServiceCollection();
        services.AddSingleton(bus);
        var sp = services.BuildServiceProvider();
        var dispatchInterceptor = new DomainEventDispatchInterceptor(sp);

        var options = new DbContextOptionsBuilder<TestDddDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;

        await using var db = new TestDddDbContext(options, auditableInterceptor, dispatchInterceptor);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TestAuditableItems" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "Title" text NOT NULL,
                "UniqueKey" text NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_TestAuditableItems_UniqueKey" ON "TestAuditableItems" ("UniqueKey");
        """);

        // 1. Success case: event published after commit
        var item1 = new TestAuditableItem(Guid.NewGuid(), "Item 1", Guid.NewGuid().ToString("N"));
        item1.TriggerEvent("msg-1");
        db.Items.Add(item1);

        await db.SaveChangesAsync();

        await bus.Received(1).InvokeAsync(Arg.Is<SampleAuditedEvent>(e => e.Message == "msg-1"), Arg.Any<CancellationToken>());
        bus.ClearReceivedCalls();

        // 2. Failure case (Review Focus #4: unique index violation): events discarded and not published on subsequent save
        var duplicateKey = "dup-" + Guid.NewGuid().ToString("N");
        var item2 = new TestAuditableItem(Guid.NewGuid(), "Item 2", duplicateKey);
        db.Items.Add(item2);
        await db.SaveChangesAsync();

        var conflictingItem = new TestAuditableItem(Guid.NewGuid(), "Item Conflict", duplicateKey);
        conflictingItem.TriggerEvent("do-not-publish");
        db.Items.Add(conflictingItem);

        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();

        // Event should NOT be dispatched on failure
        await bus.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default);

        // Detach conflicting entry
        db.Entry(conflictingItem).State = EntityState.Detached;

        // Valid subsequent save should not publish the failed event
        var validItem = new TestAuditableItem(Guid.NewGuid(), "Valid Item", Guid.NewGuid().ToString("N"));
        validItem.TriggerEvent("valid-msg");
        db.Items.Add(validItem);

        await db.SaveChangesAsync();

        await bus.Received(1).InvokeAsync(Arg.Is<SampleAuditedEvent>(e => e.Message == "valid-msg"), Arg.Any<CancellationToken>());
        await bus.DidNotReceive().InvokeAsync(Arg.Is<SampleAuditedEvent>(e => e.Message == "do-not-publish"), Arg.Any<CancellationToken>());
    }
}

