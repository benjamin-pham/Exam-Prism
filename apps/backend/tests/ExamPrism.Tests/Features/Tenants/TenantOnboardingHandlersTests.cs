namespace Tests.Features.Tenants;

using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Features.Platform.Tenants;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Tests.Infrastructure;
using Wolverine;
using Xunit;

public class TenantOnboardingHandlersTests : IClassFixture<CatalogDbFixture>
{
    private readonly CatalogDbFixture _fixture;

    public TenantOnboardingHandlersTests(CatalogDbFixture fixture)
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
    public async Task CheckTenantSlugHandler_ReturnsExpectedAvailability_ForValidInvalidReservedAndTakenSlugs()
    {
        await using var db = CreateDbContext();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var takenSlug = $"{prefix}-taken";

        db.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = takenSlug,
            Name = "Existing Organization",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var options = Options.Create(new MultiTenancyOptions
        {
            ReservedSubdomains = ["admin", "api", "www"]
        });

        var handler = new CheckTenantSlugHandler(db, options);

        // 1. Valid available slug
        var validRes = await handler.Handle(new CheckTenantSlugQuery("sunrise-academy"));
        validRes.IsSuccess.Should().BeTrue();
        validRes.Value.Slug.Should().Be("sunrise-academy");
        validRes.Value.IsAvailable.Should().BeTrue();
        validRes.Value.ReasonCode.Should().BeNull();

        // 2. Invalid slug
        var invalidRes = await handler.Handle(new CheckTenantSlugQuery("_bad_slug"));
        invalidRes.IsSuccess.Should().BeTrue();
        invalidRes.Value.IsAvailable.Should().BeFalse();
        invalidRes.Value.ReasonCode.Should().Be("INVALID_SLUG");

        // 3. Reserved slug
        var reservedRes = await handler.Handle(new CheckTenantSlugQuery("admin"));
        reservedRes.IsSuccess.Should().BeTrue();
        reservedRes.Value.IsAvailable.Should().BeFalse();
        reservedRes.Value.ReasonCode.Should().Be("RESERVED_SLUG");

        // 4. Already taken slug
        var takenRes = await handler.Handle(new CheckTenantSlugQuery(takenSlug));
        takenRes.IsSuccess.Should().BeTrue();
        takenRes.Value.IsAvailable.Should().BeFalse();
        takenRes.Value.ReasonCode.Should().Be("SLUG_ALREADY_TAKEN");
    }

    [Fact]
    public async Task CreateTenantHandler_WithValidCommand_SavesProvisioningTenantWithEncryptedPassword_InvalidatesCache_AndSendsWolverineCommand()
    {
        await using var db = CreateDbContext();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var slug = $"{prefix}-org";

        var protector = Substitute.For<IConnectionStringProtector>();
        protector.Protect(Arg.Any<string>()).Returns(call => "ENC:" + call.Arg<string>());

        var cacheInvalidator = Substitute.For<ITenantCacheInvalidator>();
        var bus = Substitute.For<IMessageBus>();
        var options = Options.Create(new MultiTenancyOptions
        {
            ReservedSubdomains = ["admin", "api", "www"]
        });
        var now = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);

        var handler = new CreateTenantHandler(db, protector, cacheInvalidator, bus, options, timeProvider);

        var command = new CreateTenantCommand(
            Slug: slug,
            Name: "New Organization",
            OwnerEmail: "owner@example.com",
            OwnerPassword: "ValidPassword123!");

        var result = await handler.Handle(command);

        result.IsSuccess.Should().BeTrue();
        result.Value.Slug.Should().Be(slug);
        result.Value.Name.Should().Be("New Organization");
        result.Value.Status.Should().Be(TenantStatus.Provisioning);
        result.Value.CreatedAt.Should().Be(now);

        // Verify entity in CatalogDbContext
        var savedTenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == result.Value.Id);
        savedTenant.Should().NotBeNull();
        savedTenant!.Slug.Should().Be(slug);
        savedTenant.Status.Should().Be(TenantStatus.Provisioning);
        savedTenant.OwnerEmail.Should().Be("owner@example.com");
        savedTenant.EncryptedOwnerPassword.Should().Be("ENC:ValidPassword123!");

        // Verify cache invalidation
        await cacheInvalidator.Received(1).InvalidateTenantAsync(result.Value.Id, slug, Arg.Any<CancellationToken>());

        // Verify message dispatch
        await bus.Received(1).SendAsync(
            Arg.Is<StartTenantProvisioningCommand>(cmd => cmd.TenantId == result.Value.Id),
            null);
    }

    [Fact]
    public async Task CreateTenantHandler_RejectsDuplicateOrReservedSlug_With409SlugAlreadyTaken()
    {
        await using var db = CreateDbContext();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var existingSlug = $"{prefix}-existing";

        db.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = existingSlug,
            Name = "Existing Org",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var protector = Substitute.For<IConnectionStringProtector>();
        var cacheInvalidator = Substitute.For<ITenantCacheInvalidator>();
        var bus = Substitute.For<IMessageBus>();
        var options = Options.Create(new MultiTenancyOptions
        {
            ReservedSubdomains = ["admin", "api", "www"]
        });
        var timeProvider = new FakeTimeProvider();

        var handler = new CreateTenantHandler(db, protector, cacheInvalidator, bus, options, timeProvider);

        // 1. Reserved subdomain
        var reservedRes = await handler.Handle(new CreateTenantCommand("admin", "Admin Org", "admin@example.com", "Password123!"));
        reservedRes.IsFailure.Should().BeTrue();
        reservedRes.Error.Code.Should().Be("SLUG_ALREADY_TAKEN");
        reservedRes.Error.Type.Should().Be(ErrorType.Conflict);

        // 2. Existing slug
        var duplicateRes = await handler.Handle(new CreateTenantCommand(existingSlug, "Duplicate Org", "dup@example.com", "Password123!"));
        duplicateRes.IsFailure.Should().BeTrue();
        duplicateRes.Error.Code.Should().Be("SLUG_ALREADY_TAKEN");
        duplicateRes.Error.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public async Task CreateTenantHandler_AndValidator_RejectWeakOwnerPassword_MissingDigitOrLowercaseOrTooShort()
    {
        await using var db = CreateDbContext();
        var protector = Substitute.For<IConnectionStringProtector>();
        var cacheInvalidator = Substitute.For<ITenantCacheInvalidator>();
        var bus = Substitute.For<IMessageBus>();
        var options = Options.Create(new MultiTenancyOptions());
        var timeProvider = new FakeTimeProvider();

        var handler = new CreateTenantHandler(db, protector, cacheInvalidator, bus, options, timeProvider);
        var validator = new CreateTenantCommandValidator();

        var initialCount = await db.Tenants.CountAsync();

        // 1. Missing digit: "NoDigitsHere"
        var noDigitCmd = new CreateTenantCommand("nodigit-org", "No Digit Org", "nodigit@example.com", "NoDigitsHere");
        var valNoDigit = validator.Validate(noDigitCmd);
        valNoDigit.IsValid.Should().BeFalse();
        valNoDigit.Errors.Should().Contain(e => e.ErrorCode == "PASSWORD_REQUIRES_DIGIT");

        var resNoDigit = await handler.Handle(noDigitCmd);
        resNoDigit.IsFailure.Should().BeTrue();
        resNoDigit.Error.Type.Should().Be(ErrorType.Validation);

        // 2. Missing lowercase: "ALLUPPER123"
        var allUpperCmd = new CreateTenantCommand("allupper-org", "All Upper Org", "upper@example.com", "ALLUPPER123");
        var valAllUpper = validator.Validate(allUpperCmd);
        valAllUpper.IsValid.Should().BeFalse();
        valAllUpper.Errors.Should().Contain(e => e.ErrorCode == "PASSWORD_REQUIRES_LOWER");

        var resAllUpper = await handler.Handle(allUpperCmd);
        resAllUpper.IsFailure.Should().BeTrue();
        resAllUpper.Error.Type.Should().Be(ErrorType.Validation);

        // 3. Shorter than 6 chars: "Ab1"
        var tooShortCmd = new CreateTenantCommand("short-org", "Short Org", "short@example.com", "Ab1");
        var valTooShort = validator.Validate(tooShortCmd);
        valTooShort.IsValid.Should().BeFalse();
        valTooShort.Errors.Should().Contain(e => e.ErrorCode == "PASSWORD_TOO_SHORT");

        var resTooShort = await handler.Handle(tooShortCmd);
        resTooShort.IsFailure.Should().BeTrue();
        resTooShort.Error.Type.Should().Be(ErrorType.Validation);

        // Ensure no rows inserted and no bus message sent
        var finalCount = await db.Tenants.CountAsync();
        finalCount.Should().Be(initialCount);
        await bus.DidNotReceiveWithAnyArgs().SendAsync<StartTenantProvisioningCommand>(default!, default);
    }

    [Fact]
    public async Task GetTenantProvisioningStatusHandler_ReturnsPublicStatus_And404WhenUnknown()
    {
        await using var db = CreateDbContext();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var slug = $"{prefix}-status-test";
        var provisionedAt = new DateTimeOffset(2026, 11, 3, 11, 30, 0, TimeSpan.Zero);

        db.Tenants.Add(new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = "Status Test Academy",
            Status = TenantStatus.Active,
            FailureReason = "Internal reason that should never leak",
            OwnerEmail = "internal@example.com",
            EncryptedConnectionString = "secret-conn",
            EncryptedOwnerPassword = "secret-password",
            CreatedAt = provisionedAt.AddMinutes(-5),
            ProvisionedAt = provisionedAt,
            UpdatedAt = provisionedAt
        });
        await db.SaveChangesAsync();

        var handler = new GetTenantProvisioningStatusHandler(db);

        // 1. Existing tenant returns public status
        var existingRes = await handler.Handle(new GetTenantProvisioningStatusQuery(slug));
        existingRes.IsSuccess.Should().BeTrue();
        existingRes.Value.Slug.Should().Be(slug);
        existingRes.Value.Name.Should().Be("Status Test Academy");
        existingRes.Value.Status.Should().Be(TenantStatus.Active);
        existingRes.Value.ProvisionedAt.Should().Be(provisionedAt);

        // 2. Unknown tenant returns 404 Not Found
        var unknownRes = await handler.Handle(new GetTenantProvisioningStatusQuery("unknown-nonexistent-slug"));
        unknownRes.IsFailure.Should().BeTrue();
        unknownRes.Error.Code.Should().Be("TENANT_NOT_FOUND");
        unknownRes.Error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task RetryTenantProvisioningHandler_TransitionsFailedToProvisioning_ClearsFailureReason_AndReSendsCommand()
    {
        await using var db = CreateDbContext();
        var prefix = Guid.NewGuid().ToString("N")[..8];

        var failedTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-failed",
            Name = "Failed Academy",
            Status = TenantStatus.Failed,
            FailureReason = "Database creation timeout",
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        var activeTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-active",
            Name = "Active Academy",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-2)
        };

        var suspendedTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-suspended",
            Name = "Suspended Academy",
            Status = TenantStatus.Suspended,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-3),
            UpdatedAt = DateTimeOffset.UtcNow.AddHours(-3)
        };

        db.Tenants.AddRange(failedTenant, activeTenant, suspendedTenant);
        await db.SaveChangesAsync();

        var cacheInvalidator = Substitute.For<ITenantCacheInvalidator>();
        var bus = Substitute.For<IMessageBus>();
        var now = new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero);
        var timeProvider = new FakeTimeProvider(now);

        var handler = new RetryTenantProvisioningHandler(db, cacheInvalidator, bus, timeProvider);

        // 1. Failed tenant -> transitions to Provisioning, clears failure reason, enqueues command
        var retryRes = await handler.Handle(new RetryTenantProvisioningCommand(failedTenant.Id));
        retryRes.IsSuccess.Should().BeTrue();
        retryRes.Value.Id.Should().Be(failedTenant.Id);
        retryRes.Value.Status.Should().Be(TenantStatus.Provisioning);
        retryRes.Value.FailureReason.Should().BeNull();
        retryRes.Value.UpdatedAt.Should().Be(now);

        var reloadedFailed = await db.Tenants.FirstAsync(t => t.Id == failedTenant.Id);
        reloadedFailed.Status.Should().Be(TenantStatus.Provisioning);
        reloadedFailed.FailureReason.Should().BeNull();
        reloadedFailed.UpdatedAt.Should().Be(now);

        await cacheInvalidator.Received(1).InvalidateTenantAsync(failedTenant.Id, failedTenant.Slug, Arg.Any<CancellationToken>());
        await bus.Received(1).SendAsync(
            Arg.Is<StartTenantProvisioningCommand>(cmd => cmd.TenantId == failedTenant.Id),
            null);

        // 2. Active tenant -> returns 409 INVALID_TENANT_STATUS_TRANSITION
        var activeRes = await handler.Handle(new RetryTenantProvisioningCommand(activeTenant.Id));
        activeRes.IsFailure.Should().BeTrue();
        activeRes.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");
        activeRes.Error.Type.Should().Be(ErrorType.Conflict);

        // 3. Suspended tenant -> returns 409 INVALID_TENANT_STATUS_TRANSITION
        var suspendedRes = await handler.Handle(new RetryTenantProvisioningCommand(suspendedTenant.Id));
        suspendedRes.IsFailure.Should().BeTrue();
        suspendedRes.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");
        suspendedRes.Error.Type.Should().Be(ErrorType.Conflict);

        // 4. Unknown tenant -> returns 404 TENANT_NOT_FOUND
        var unknownRes = await handler.Handle(new RetryTenantProvisioningCommand(Guid.NewGuid()));
        unknownRes.IsFailure.Should().BeTrue();
        unknownRes.Error.Code.Should().Be("TENANT_NOT_FOUND");
        unknownRes.Error.Type.Should().Be(ErrorType.NotFound);
    }
}
