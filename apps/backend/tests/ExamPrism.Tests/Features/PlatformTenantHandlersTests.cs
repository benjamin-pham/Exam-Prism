namespace Tests.Features;

using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Features.Platform.Tenants;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Tests.Infrastructure;
using Xunit;

public class PlatformTenantHandlersTests : IClassFixture<CatalogDbFixture>
{
    private readonly CatalogDbFixture _fixture;

    public PlatformTenantHandlersTests(CatalogDbFixture fixture)
    {
        _fixture = fixture;
    }

    private CatalogDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .Options;
        return new CatalogDbContext(options);
    }

    [Fact]
    public async Task ListTenantsHandler_FiltersByStatusAndSearch_AndPaginatesOrderedByCreatedAtDesc()
    {
        await using var db = CreateDbContext();
        var baseTime = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        var prefix = Guid.NewGuid().ToString("N")[..8];

        var t1 = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-alpha",
            Name = $"{prefix} Alpha University",
            Status = TenantStatus.Active,
            CreatedAt = baseTime.AddHours(1),
            UpdatedAt = baseTime.AddHours(1)
        };
        var t2 = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-beta",
            Name = $"{prefix} Beta College",
            Status = TenantStatus.Active,
            CreatedAt = baseTime.AddHours(2),
            UpdatedAt = baseTime.AddHours(2)
        };
        var t3 = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-gamma",
            Name = $"{prefix} Gamma Academy",
            Status = TenantStatus.Suspended,
            CreatedAt = baseTime.AddHours(3),
            UpdatedAt = baseTime.AddHours(3)
        };
        var t4 = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-delta",
            Name = $"{prefix} Delta Institute",
            Status = TenantStatus.Failed,
            CreatedAt = baseTime.AddHours(4),
            UpdatedAt = baseTime.AddHours(4)
        };

        db.Tenants.AddRange(t1, t2, t3, t4);
        await db.SaveChangesAsync();

        var handler = new ListTenantsHandler(db);

        typeof(ListTenantsHandler).Assembly
            .GetType("ExamPrism.Features.Platform.Tenants.ListTenantsDbQuery")
            .Should().BeNull();

        // 0. Nullable parameters (Status: null, Search: null)
        var unfilteredRes = await handler.Handle(new ListTenantsQuery());
        unfilteredRes.IsSuccess.Should().BeTrue();
        unfilteredRes.Value.TotalCount.Should().BeGreaterOrEqualTo(4);

        // 1. Filter by Status = Active
        var activeRes = await handler.Handle(new ListTenantsQuery(Status: TenantStatus.Active, Search: prefix));
        activeRes.IsSuccess.Should().BeTrue();
        activeRes.Value.TotalCount.Should().Be(2);
        activeRes.Value.Items.Should().HaveCount(2);
        activeRes.Value.Items.Should().OnlyContain(t => t.Status == TenantStatus.Active);
        activeRes.Value.Items[0].Id.Should().Be(t2.Id); // t2 has later CreatedAt than t1
        activeRes.Value.Items[1].Id.Should().Be(t1.Id);

        // 2. Case-insensitive Search on Name
        var searchNameRes = await handler.Handle(new ListTenantsQuery(Search: "COLLEGE"));
        searchNameRes.IsSuccess.Should().BeTrue();
        searchNameRes.Value.Items.Should().ContainSingle(t => t.Id == t2.Id);

        // 3. Case-insensitive Search on Slug
        var searchSlugRes = await handler.Handle(new ListTenantsQuery(Search: $"{prefix}-gamma"));
        searchSlugRes.IsSuccess.Should().BeTrue();
        searchSlugRes.Value.Items.Should().ContainSingle(t => t.Id == t3.Id);

        // 4. Pagination slicing
        var pagedRes = await handler.Handle(new ListTenantsQuery(Page: 2, PageSize: 2, Search: prefix));
        pagedRes.IsSuccess.Should().BeTrue();
        pagedRes.Value.TotalCount.Should().Be(4);
        pagedRes.Value.Page.Should().Be(2);
        pagedRes.Value.PageSize.Should().Be(2);
        pagedRes.Value.Items.Should().HaveCount(2);
        // Ordered by CreatedAt desc: t4, t3, t2, t1. Page 2 has t2, t1.
        pagedRes.Value.Items[0].Id.Should().Be(t2.Id);
        pagedRes.Value.Items[1].Id.Should().Be(t1.Id);
    }

    [Fact]
    public async Task GetTenantByIdHandler_ReturnsDetailWhenFound_AndNotFoundWhenMissing()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"tenant-{Guid.NewGuid():N}",
            Name = "Inspectable Tenant",
            Status = TenantStatus.Provisioning,
            FailureReason = "Step 2 failed",
            CreatedAt = now,
            ProvisionedAt = null,
            UpdatedAt = now
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var handler = new GetTenantByIdHandler(db);

        // Found
        var foundResult = await handler.Handle(new GetTenantByIdQuery(tenant.Id));
        foundResult.IsSuccess.Should().BeTrue();
        foundResult.Value.Should().NotBeNull();
        foundResult.Value.Id.Should().Be(tenant.Id);
        foundResult.Value.Slug.Should().Be(tenant.Slug);
        foundResult.Value.Name.Should().Be("Inspectable Tenant");
        foundResult.Value.Status.Should().Be(TenantStatus.Provisioning);
        foundResult.Value.FailureReason.Should().Be("Step 2 failed");
        foundResult.Value.CreatedAt.Should().BeCloseTo(tenant.CreatedAt, TimeSpan.FromMilliseconds(1));
        foundResult.Value.ProvisionedAt.Should().BeNull();
        foundResult.Value.UpdatedAt.Should().BeCloseTo(tenant.UpdatedAt, TimeSpan.FromMilliseconds(1));

        // Not found
        var missingResult = await handler.Handle(new GetTenantByIdQuery(Guid.NewGuid()));
        missingResult.IsFailure.Should().BeTrue();
        missingResult.Error.Code.Should().Be("TENANT_NOT_FOUND");
        missingResult.Error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task UpdateTenantHandler_TrimsAndUpdatesName_AndInvalidatesCache()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"tenant-upd-{Guid.NewGuid():N}",
            Name = "Old Name",
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var cacheInvalidator = Substitute.For<ITenantCacheInvalidator>();
        var updatedTime = now.AddHours(2);
        var timeProvider = new FakeTimeProvider(updatedTime);

        var handler = new UpdateTenantHandler(db, cacheInvalidator, timeProvider);

        var result = await handler.Handle(new UpdateTenantCommand(tenant.Id, "   Renamed Organization   "));
        result.IsSuccess.Should().BeTrue();
        result.Value.Name.Should().Be("Renamed Organization");
        result.Value.UpdatedAt.Should().Be(updatedTime);

        var reloaded = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenant.Id);
        reloaded.Name.Should().Be("Renamed Organization");
        reloaded.UpdatedAt.Should().BeCloseTo(updatedTime, TimeSpan.FromMilliseconds(1));

        await cacheInvalidator.Received(1).InvalidateTenantAsync(tenant.Id, tenant.Slug, Arg.Any<CancellationToken>());

        // Missing tenant returns 404
        var missingRes = await handler.Handle(new UpdateTenantCommand(Guid.NewGuid(), "Some Name"));
        missingRes.IsFailure.Should().BeTrue();
        missingRes.Error.Code.Should().Be("TENANT_NOT_FOUND");
    }

    [Fact]
    public async Task SuspendTenantHandler_TransitionsActiveToSuspended_InvalidatesCache_AndRejectsNonActiveTenants()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var activeTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"active-suspend-{Guid.NewGuid():N}",
            Name = "Active Org",
            Status = TenantStatus.Active,
            EncryptedConnectionString = "enc-conn",
            CreatedAt = now,
            UpdatedAt = now
        };
        var provisioningTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"prov-suspend-{Guid.NewGuid():N}",
            Name = "Prov Org",
            Status = TenantStatus.Provisioning,
            CreatedAt = now,
            UpdatedAt = now
        };
        var failedTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"fail-suspend-{Guid.NewGuid():N}",
            Name = "Fail Org",
            Status = TenantStatus.Failed,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Tenants.AddRange(activeTenant, provisioningTenant, failedTenant);
        await db.SaveChangesAsync();

        var cacheInvalidator = Substitute.For<ITenantCacheInvalidator>();
        var suspendTime = now.AddMinutes(30);
        var timeProvider = new FakeTimeProvider(suspendTime);

        var handler = new SuspendTenantHandler(db, cacheInvalidator, timeProvider);

        // 1. Suspend Active tenant -> succeeds and invalidates cache
        var suspendResult = await handler.Handle(new SuspendTenantCommand(activeTenant.Id));
        suspendResult.IsSuccess.Should().BeTrue();
        suspendResult.Value.Status.Should().Be(TenantStatus.Suspended);
        suspendResult.Value.UpdatedAt.Should().Be(suspendTime);

        var reloaded = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == activeTenant.Id);
        reloaded.Status.Should().Be(TenantStatus.Suspended);

        await cacheInvalidator.Received(1).InvalidateTenantAsync(activeTenant.Id, activeTenant.Slug, Arg.Any<CancellationToken>());

        // 2. Suspending already Suspended tenant -> 409 Conflict
        var secondSuspendResult = await handler.Handle(new SuspendTenantCommand(activeTenant.Id));
        secondSuspendResult.IsFailure.Should().BeTrue();
        secondSuspendResult.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");
        secondSuspendResult.Error.Type.Should().Be(ErrorType.Conflict);

        // 3. Suspending Provisioning tenant -> 409 Conflict
        var provResult = await handler.Handle(new SuspendTenantCommand(provisioningTenant.Id));
        provResult.IsFailure.Should().BeTrue();
        provResult.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");
        provResult.Error.Type.Should().Be(ErrorType.Conflict);

        // 4. Suspending Failed tenant -> 409 Conflict
        var failResult = await handler.Handle(new SuspendTenantCommand(failedTenant.Id));
        failResult.IsFailure.Should().BeTrue();
        failResult.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");
        failResult.Error.Type.Should().Be(ErrorType.Conflict);

        // 5. Unknown ID -> 404 Not Found
        var unknownResult = await handler.Handle(new SuspendTenantCommand(Guid.NewGuid()));
        unknownResult.IsFailure.Should().BeTrue();
        unknownResult.Error.Code.Should().Be("TENANT_NOT_FOUND");
    }

    [Fact]
    public async Task ReactivateTenantHandler_TransitionsSuspendedWithConnectionStringToActive_InvalidatesCache_AndRejectsInvalidStates()
    {
        await using var db = CreateDbContext();
        var now = DateTime.UtcNow;
        var suspendedValidTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"susp-valid-{Guid.NewGuid():N}",
            Name = "Suspended Valid Org",
            Status = TenantStatus.Suspended,
            EncryptedConnectionString = "valid-encrypted-connection-string",
            CreatedAt = now,
            UpdatedAt = now
        };
        var suspendedNoConnTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"susp-noconn-{Guid.NewGuid():N}",
            Name = "Suspended No Conn Org",
            Status = TenantStatus.Suspended,
            EncryptedConnectionString = null,
            CreatedAt = now,
            UpdatedAt = now
        };
        var suspendedBlankConnTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"susp-blankconn-{Guid.NewGuid():N}",
            Name = "Suspended Blank Conn Org",
            Status = TenantStatus.Suspended,
            EncryptedConnectionString = "   ",
            CreatedAt = now,
            UpdatedAt = now
        };
        var activeTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"active-react-{Guid.NewGuid():N}",
            Name = "Active Org",
            Status = TenantStatus.Active,
            EncryptedConnectionString = "valid-conn",
            CreatedAt = now,
            UpdatedAt = now
        };
        var provisioningTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"prov-react-{Guid.NewGuid():N}",
            Name = "Prov Org",
            Status = TenantStatus.Provisioning,
            EncryptedConnectionString = null,
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Tenants.AddRange(suspendedValidTenant, suspendedNoConnTenant, suspendedBlankConnTenant, activeTenant, provisioningTenant);
        await db.SaveChangesAsync();

        var cacheInvalidator = Substitute.For<ITenantCacheInvalidator>();
        var reactivateTime = now.AddHours(1);
        var timeProvider = new FakeTimeProvider(reactivateTime);

        var handler = new ReactivateTenantHandler(db, cacheInvalidator, timeProvider);

        // 1. Reactivate Suspended with connection string -> succeeds and invalidates cache
        var reactResult = await handler.Handle(new ReactivateTenantCommand(suspendedValidTenant.Id));
        reactResult.IsSuccess.Should().BeTrue();
        reactResult.Value.Status.Should().Be(TenantStatus.Active);
        reactResult.Value.UpdatedAt.Should().Be(reactivateTime);

        var reloaded = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == suspendedValidTenant.Id);
        reloaded.Status.Should().Be(TenantStatus.Active);

        await cacheInvalidator.Received(1).InvalidateTenantAsync(suspendedValidTenant.Id, suspendedValidTenant.Slug, Arg.Any<CancellationToken>());

        // 2. Reactivating already Active tenant -> 409 Conflict
        var secondReactResult = await handler.Handle(new ReactivateTenantCommand(suspendedValidTenant.Id));
        secondReactResult.IsFailure.Should().BeTrue();
        secondReactResult.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");
        secondReactResult.Error.Type.Should().Be(ErrorType.Conflict);

        // 3. Reactivating Suspended with null connection string -> 409 Conflict
        var noConnResult = await handler.Handle(new ReactivateTenantCommand(suspendedNoConnTenant.Id));
        noConnResult.IsFailure.Should().BeTrue();
        noConnResult.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");
        noConnResult.Error.Type.Should().Be(ErrorType.Conflict);

        // 4. Reactivating Suspended with blank connection string -> 409 Conflict
        var blankConnResult = await handler.Handle(new ReactivateTenantCommand(suspendedBlankConnTenant.Id));
        blankConnResult.IsFailure.Should().BeTrue();
        blankConnResult.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");
        blankConnResult.Error.Type.Should().Be(ErrorType.Conflict);

        // 5. Reactivating Provisioning tenant -> 409 Conflict
        var provResult = await handler.Handle(new ReactivateTenantCommand(provisioningTenant.Id));
        provResult.IsFailure.Should().BeTrue();
        provResult.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");
        provResult.Error.Type.Should().Be(ErrorType.Conflict);

        // 6. Unknown ID -> 404 Not Found
        var unknownResult = await handler.Handle(new ReactivateTenantCommand(Guid.NewGuid()));
        unknownResult.IsFailure.Should().BeTrue();
        unknownResult.Error.Code.Should().Be("TENANT_NOT_FOUND");
    }

    [Theory]
    [InlineData(0, 20, false, "INVALID_PAGE")]
    [InlineData(-1, 20, false, "INVALID_PAGE")]
    [InlineData(1, 0, false, "INVALID_PAGE_SIZE")]
    [InlineData(1, 101, false, "INVALID_PAGE_SIZE")]
    [InlineData(1, 20, true, null)]
    [InlineData(1, 100, true, null)]
    public void ListTenantsQueryValidator_ValidatesPageAndPageSize(int page, int pageSize, bool isValid, string? expectedErrorCode)
    {
        var validator = new ListTenantsQueryValidator();
        var result = validator.Validate(new ListTenantsQuery(page, pageSize));

        result.IsValid.Should().Be(isValid);
        if (!isValid)
        {
            result.Errors.Should().Contain(e => e.ErrorCode == expectedErrorCode);
        }
    }

    [Theory]
    [InlineData("", false, "REQUIRED")]
    [InlineData("   ", false, "REQUIRED")]
    [InlineData("Valid Tenant Name", true, null)]
    public void UpdateTenantRequestValidator_ValidatesName(string name, bool isValid, string? expectedErrorCode)
    {
        var validator = new UpdateTenantRequestValidator();
        var result = validator.Validate(new UpdateTenantRequest(name));

        result.IsValid.Should().Be(isValid);
        if (!isValid)
        {
            result.Errors.Should().Contain(e => e.ErrorCode == expectedErrorCode);
        }
    }

    [Fact]
    public void UpdateTenantRequestValidator_RejectsNameExceedingMaxLength()
    {
        var validator = new UpdateTenantRequestValidator();
        var longName = new string('A', 201);
        var result = validator.Validate(new UpdateTenantRequest(longName));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorCode == "MAX_LENGTH");
    }

    [Fact]
    public void UpdateTenantCommandValidator_ValidatesIdAndName()
    {
        var validator = new UpdateTenantCommandValidator();

        var emptyIdResult = validator.Validate(new UpdateTenantCommand(Guid.Empty, "Valid Name"));
        emptyIdResult.IsValid.Should().BeFalse();
        emptyIdResult.Errors.Should().Contain(e => e.ErrorCode == "REQUIRED");

        var emptyNameResult = validator.Validate(new UpdateTenantCommand(Guid.NewGuid(), ""));
        emptyNameResult.IsValid.Should().BeFalse();
        emptyNameResult.Errors.Should().Contain(e => e.ErrorCode == "REQUIRED");

        var longNameResult = validator.Validate(new UpdateTenantCommand(Guid.NewGuid(), new string('x', 201)));
        longNameResult.IsValid.Should().BeFalse();
        longNameResult.Errors.Should().Contain(e => e.ErrorCode == "MAX_LENGTH");

        var validResult = validator.Validate(new UpdateTenantCommand(Guid.NewGuid(), "Good Org Name"));
        validResult.IsValid.Should().BeTrue();
    }
}
