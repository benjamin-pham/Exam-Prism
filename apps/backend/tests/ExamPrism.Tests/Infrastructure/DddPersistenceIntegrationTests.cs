namespace Tests.Infrastructure;

using System.Linq.Expressions;
using Common.Domain;
using Common.Domain.Specifications;
using Common.Persistence;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using ExamPrism.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private sealed class CustomReadOnlyCatalogTenantCountQuery(string prefix) : IDbQuery<ReadOnlyCatalogDbContext, int>
    {
        public Task<int> ExecuteAsync(ReadOnlyCatalogDbContext db, CancellationToken ct = default)
        {
            return db.Tenants.CountAsync(t => t.Slug.StartsWith(prefix), ct);
        }
    }

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

    private sealed class TenantNameProjectionSpec : Specification<Tenant, string>
    {
        public TenantNameProjectionSpec(string prefix)
        {
            Where(t => t.Slug.StartsWith(prefix));
            ApplyOrderBy(t => t.Slug);
            Select(t => t.Name);
        }
    }

    [Fact]
    public async Task DbContextQueryExtensions_ExecutesIDbQuery_AndSpecificationMethods_OnWriteAndReadOnlyContexts()
    {
        await using var db = CreateCatalogDbContext();
        var prefix = "query-test-" + Guid.NewGuid().ToString("N")[..6];

        var t1 = new Tenant { Id = Guid.NewGuid(), Slug = $"{prefix}-1", Name = "Tenant One", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var t2 = new Tenant { Id = Guid.NewGuid(), Slug = $"{prefix}-2", Name = "Tenant Two", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        var t3 = new Tenant { Id = Guid.NewGuid(), Slug = $"{prefix}-3", Name = "Tenant Three", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };

        db.Tenants.AddRange(t1, t2, t3);
        await db.SaveChangesAsync();

        using var readOnlyDb = new ReadOnlyCatalogDbContext(CreateCatalogDbContext());

        // 1. IDbQuery
        var countWrite = await db.QueryAsync(new CustomCatalogTenantCountQuery(prefix));
        countWrite.Should().Be(3);

        var countRead = await readOnlyDb.QueryAsync(new CustomReadOnlyCatalogTenantCountQuery(prefix));
        countRead.Should().Be(3);

        // 2. QueryFirstOrDefaultAsync (Entity & Projection)
        var firstSpec = new TenantByPrefixSpec(prefix);
        var firstEntity = await db.QueryFirstOrDefaultAsync(firstSpec);
        firstEntity.Should().NotBeNull();
        firstEntity!.Slug.Should().Be($"{prefix}-1");

        var firstReadEntity = await readOnlyDb.QueryFirstOrDefaultAsync(firstSpec);
        firstReadEntity.Should().NotBeNull();
        firstReadEntity!.Slug.Should().Be($"{prefix}-1");

        var projSpec = new TenantNameProjectionSpec(prefix);
        var firstName = await db.QueryFirstOrDefaultAsync(projSpec);
        firstName.Should().Be("Tenant One");

        var firstReadName = await readOnlyDb.QueryFirstOrDefaultAsync(projSpec);
        firstReadName.Should().Be("Tenant One");

        // 3. QueryListAsync (Entity & Projection)
        var listEntities = await db.QueryListAsync(firstSpec);
        listEntities.Should().HaveCount(3);

        var listNames = await readOnlyDb.QueryListAsync(projSpec);
        listNames.Should().Equal("Tenant One", "Tenant Two", "Tenant Three");

        // 4. QueryAnyAsync & QueryCountAsync (Review Focus #3: Paged specification ignores Skip/Take for Count)
        var pagedSpec = new PagedTenantSpec(prefix, skip: 0, take: 1);
        var pagedList = await db.QueryListAsync(pagedSpec);
        pagedList.Should().HaveCount(1);

        var exists = await db.QueryAnyAsync(pagedSpec);
        exists.Should().BeTrue();

        var totalCount = await db.QueryCountAsync(pagedSpec);
        totalCount.Should().Be(3);

        var readTotalCount = await readOnlyDb.QueryCountAsync(pagedSpec);
        readTotalCount.Should().Be(3);
    }

    public sealed record SampleAuditedEvent(string Message) : DomainEventBase;

    public sealed class TestAuditableItem : AggregateRoot<Guid>, IAuditableEntity
    {
        public string Title { get; set; } = string.Empty;
        public string UniqueKey { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; private set; }
        public DateTimeOffset UpdatedAt { get; private set; }

        public TestAuditableItem() { }
        public TestAuditableItem(Guid id, string title, string uniqueKey) : base(id)
        {
            Title = title;
            UniqueKey = uniqueKey;
        }

        public void TriggerEvent(string msg) => RaiseDomainEvent(new SampleAuditedEvent(msg));
        public void SetCreated(DateTimeOffset now) => CreatedAt = now;
        public void SetUpdated(DateTimeOffset now) => UpdatedAt = now;
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
        var now = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
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

