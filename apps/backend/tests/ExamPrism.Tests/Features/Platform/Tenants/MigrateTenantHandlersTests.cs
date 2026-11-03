namespace Tests.Features.Platform.Tenants;

using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Features.Platform.Tenants;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Tests.Infrastructure;
using Xunit;

public class MigrateTenantHandlersTests : IClassFixture<CatalogDbFixture>
{
    private readonly CatalogDbFixture _fixture;

    public MigrateTenantHandlersTests(CatalogDbFixture fixture)
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
    public async Task MigrateAllTenantsHandler_DelegatesToFleetMigrator_AndReturnsSummary()
    {
        // Arrange
        var fleetMigrator = Substitute.For<ITenantFleetMigrator>();
        var expectedSummary = new FleetMigrationSummaryResult(
            TotalTenants: 2,
            SucceededCount: 2,
            FailedCount: 0,
            TotalDurationMs: 150,
            Results:
            [
                new TenantMigrationItemResult(Guid.NewGuid(), "tenant-a", Succeeded: true, AppliedMigrationsCount: 3, ErrorMessage: null, DurationMs: 75),
                new TenantMigrationItemResult(Guid.NewGuid(), "tenant-b", Succeeded: true, AppliedMigrationsCount: 3, ErrorMessage: null, DurationMs: 75)
            ]);

        fleetMigrator.MigrateAllActiveTenantsAsync(Arg.Any<CancellationToken>())
            .Returns(expectedSummary);

        var handler = new MigrateAllTenantsHandler(fleetMigrator);

        // Act
        var result = await handler.Handle(new MigrateAllTenantsCommand(), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expectedSummary);
        result.Value.TotalTenants.Should().Be(2);
        result.Value.SucceededCount.Should().Be(2);
        result.Value.FailedCount.Should().Be(0);

        await fleetMigrator.Received(1).MigrateAllActiveTenantsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MigrateSingleTenantHandler_Returns404WhenMissing_409WhenNotActive_AndDelegatesWhenActive()
    {
        // Arrange
        await using var db = CreateDbContext();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var baseTime = DateTimeOffset.UtcNow;

        var provisioningTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-prov",
            Name = "Prov Org",
            Status = TenantStatus.Provisioning,
            EncryptedConnectionString = null,
            CreatedAt = baseTime,
            UpdatedAt = baseTime
        };

        var failedTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-failed",
            Name = "Failed Org",
            Status = TenantStatus.Failed,
            FailureReason = "Database creation failed",
            EncryptedConnectionString = null,
            CreatedAt = baseTime,
            UpdatedAt = baseTime
        };

        var activeNoConnTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-noconn",
            Name = "No Conn Org",
            Status = TenantStatus.Active,
            EncryptedConnectionString = "   ",
            CreatedAt = baseTime,
            UpdatedAt = baseTime
        };

        var activeTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-active",
            Name = "Active Org",
            Status = TenantStatus.Active,
            EncryptedConnectionString = "ENC:protected_connection_string",
            CreatedAt = baseTime,
            UpdatedAt = baseTime,
            ProvisionedAt = baseTime
        };

        db.Tenants.AddRange(provisioningTenant, failedTenant, activeNoConnTenant, activeTenant);
        await db.SaveChangesAsync();

        var fleetMigrator = Substitute.For<ITenantFleetMigrator>();
        var expectedItemResult = new TenantMigrationItemResult(
            TenantId: activeTenant.Id,
            Slug: activeTenant.Slug,
            Succeeded: true,
            AppliedMigrationsCount: 2,
            ErrorMessage: null,
            DurationMs: 42);

        fleetMigrator.MigrateTenantAsync(activeTenant.Id, Arg.Any<CancellationToken>())
            .Returns(expectedItemResult);

        var handler = new MigrateSingleTenantHandler(db, fleetMigrator);

        // 1. Missing tenant -> 404 TENANT_NOT_FOUND
        var missingRes = await handler.Handle(new MigrateSingleTenantCommand(Guid.NewGuid()), CancellationToken.None);
        missingRes.IsFailure.Should().BeTrue();
        missingRes.Error.Code.Should().Be("TENANT_NOT_FOUND");
        missingRes.Error.Message.Should().Be("The requested organization was not found.");

        // 2. Provisioning tenant -> 409 INVALID_TENANT_STATUS_TRANSITION
        var provRes = await handler.Handle(new MigrateSingleTenantCommand(provisioningTenant.Id), CancellationToken.None);
        provRes.IsFailure.Should().BeTrue();
        provRes.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");

        // 3. Failed tenant -> 409 INVALID_TENANT_STATUS_TRANSITION
        var failedRes = await handler.Handle(new MigrateSingleTenantCommand(failedTenant.Id), CancellationToken.None);
        failedRes.IsFailure.Should().BeTrue();
        failedRes.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");

        // 4. Active tenant with whitespace connection string -> 409 INVALID_TENANT_STATUS_TRANSITION
        var noConnRes = await handler.Handle(new MigrateSingleTenantCommand(activeNoConnTenant.Id), CancellationToken.None);
        noConnRes.IsFailure.Should().BeTrue();
        noConnRes.Error.Code.Should().Be("INVALID_TENANT_STATUS_TRANSITION");

        // 5. Active tenant with configured connection string -> 200 OK + delegates to fleet migrator
        var activeRes = await handler.Handle(new MigrateSingleTenantCommand(activeTenant.Id), CancellationToken.None);
        activeRes.IsSuccess.Should().BeTrue();
        activeRes.Value.Should().Be(expectedItemResult);
        activeRes.Value.TenantId.Should().Be(activeTenant.Id);
        activeRes.Value.Slug.Should().Be(activeTenant.Slug);
        activeRes.Value.Succeeded.Should().BeTrue();

        await fleetMigrator.Received(1).MigrateTenantAsync(activeTenant.Id, Arg.Any<CancellationToken>());
    }
}
