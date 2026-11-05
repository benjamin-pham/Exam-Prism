namespace Tests.Infrastructure;

using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

public class TenantProvisionerAndFleetMigratorTests : IClassFixture<CatalogDbFixture>
{
    private readonly CatalogDbFixture _fixture;

    public TenantProvisionerAndFleetMigratorTests(CatalogDbFixture fixture)
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

    [Fact]
    public async Task StartTenantProvisioningHandler_Handle_DelegatesToTenantProvisionerAndReturnsSuccess()
    {
        // Arrange
        var provisioner = Substitute.For<ITenantProvisioner>();
        var handler = new StartTenantProvisioningHandler(provisioner);
        var tenantId = Guid.NewGuid();
        var command = new StartTenantProvisioningCommand(tenantId);

        // Act
        var result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        await provisioner.Received(1).ProvisionAsync(tenantId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantProvisioner_WhenTenantDoesNotExistOrIsAlreadyActive_NoOpsGracefully()
    {
        // Arrange
        await using var db = CreateCatalogDbContext();
        var baseTime = DateTime.UtcNow;
        var prefix = Guid.NewGuid().ToString("N")[..8];

        var activeTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-active",
            Name = "Active Org",
            Status = TenantStatus.Active,
            CreatedAt = baseTime,
            UpdatedAt = baseTime
        };
        var suspendedTenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-suspended",
            Name = "Suspended Org",
            Status = TenantStatus.Suspended,
            CreatedAt = baseTime,
            UpdatedAt = baseTime
        };

        db.Tenants.AddRange(activeTenant, suspendedTenant);
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder().Build();
        var options = Options.Create(new MultiTenancyOptions());
        var protector = Substitute.For<IConnectionStringProtector>();
        var normalizer = Substitute.For<ITenantConnectionStringNormalizer>();
        var cacheInvalidator = Substitute.For<ITenantCacheInvalidator>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = Substitute.For<ILogger<TenantProvisioner>>();

        var provisioner = new TenantProvisioner(
            db,
            configuration,
            options,
            protector,
            normalizer,
            cacheInvalidator,
            scopeFactory,
            TimeProvider.System,
            logger);

        // Act - Non-existent tenant
        var nonExistentId = Guid.NewGuid();
        var actMissing = async () => await provisioner.ProvisionAsync(nonExistentId, CancellationToken.None);
        await actMissing.Should().NotThrowAsync();

        // Act - Active tenant
        var actActive = async () => await provisioner.ProvisionAsync(activeTenant.Id, CancellationToken.None);
        await actActive.Should().NotThrowAsync();

        // Act - Suspended tenant
        var actSuspended = async () => await provisioner.ProvisionAsync(suspendedTenant.Id, CancellationToken.None);
        await actSuspended.Should().NotThrowAsync();

        // Assert - states unchanged
        var reloadedActive = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == activeTenant.Id);
        reloadedActive.Status.Should().Be(TenantStatus.Active);

        var reloadedSuspended = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == suspendedTenant.Id);
        reloadedSuspended.Status.Should().Be(TenantStatus.Suspended);

        await cacheInvalidator.DidNotReceiveWithAnyArgs().InvalidateTenantAsync(default, default!, default);
    }

    [Fact]
    public async Task TenantProvisioner_WhenServerConnectionFails_TransitionsTenantToFailed_PreservesEncryptedOwnerPassword_AndInvalidatesCache()
    {
        // Arrange
        await using var db = CreateCatalogDbContext();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var baseTime = DateTime.UtcNow;

        var protector = Substitute.For<IConnectionStringProtector>();
        protector.Protect("OwnerPass123!").Returns("EncryptedOwnerPass123!");
        protector.Unprotect("EncryptedOwnerPass123!").Returns("OwnerPass123!");

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-fail",
            Name = "Fail Org",
            Status = TenantStatus.Provisioning,
            OwnerEmail = "owner@fail.edu",
            EncryptedOwnerPassword = protector.Protect("OwnerPass123!"),
            CreatedAt = baseTime,
            UpdatedAt = baseTime
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var multiTenancyOptions = new MultiTenancyOptions
        {
            Provisioning = new TenantProvisioningOptions
            {
                ServerConnectionString = "Host=127.0.0.1;Port=1;Database=postgres;Username=postgres;Password=postgres;Timeout=1;Command Timeout=1"
            }
        };

        var configuration = new ConfigurationBuilder().Build();
        var options = Options.Create(multiTenancyOptions);
        var normalizer = Substitute.For<ITenantConnectionStringNormalizer>();
        var cacheInvalidator = Substitute.For<ITenantCacheInvalidator>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var logger = Substitute.For<ILogger<TenantProvisioner>>();

        var provisioner = new TenantProvisioner(
            db,
            configuration,
            options,
            protector,
            normalizer,
            cacheInvalidator,
            scopeFactory,
            TimeProvider.System,
            logger);

        // Act
        await provisioner.ProvisionAsync(tenant.Id, CancellationToken.None);

        // Assert
        var reloaded = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id);
        reloaded.Status.Should().Be(TenantStatus.Failed);
        reloaded.FailureReason.Should().NotBeNullOrWhiteSpace();
        reloaded.EncryptedOwnerPassword.Should().Be("EncryptedOwnerPass123!", "EncryptedOwnerPassword must be preserved on failure for retry");
        reloaded.UpdatedAt.Should().BeAfter(baseTime);

        await cacheInvalidator.Received(1).InvalidateTenantAsync(tenant.Id, tenant.Slug, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TenantFleetMigrator_MigrateTenantAsync_WhenTenantMissingOrMissingConnectionString_ReturnsFailedItemWithoutThrowing()
    {
        // Arrange
        await using var db = CreateCatalogDbContext();
        var prefix = Guid.NewGuid().ToString("N")[..8];
        var baseTime = DateTime.UtcNow;

        var tenantWithoutConnStr = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = $"{prefix}-noconn",
            Name = "No Conn Org",
            Status = TenantStatus.Active,
            EncryptedConnectionString = null,
            CreatedAt = baseTime,
            UpdatedAt = baseTime
        };

        db.Tenants.Add(tenantWithoutConnStr);
        await db.SaveChangesAsync();

        var protector = Substitute.For<IConnectionStringProtector>();
        var normalizer = Substitute.For<ITenantConnectionStringNormalizer>();
        var options = Options.Create(new MultiTenancyOptions());
        var logger = Substitute.For<ILogger<TenantFleetMigrator>>();

        var migrator = new TenantFleetMigrator(
            db,
            protector,
            normalizer,
            options,
            logger);

        // Act - missing tenant
        var missingResult = await migrator.MigrateTenantAsync(Guid.NewGuid(), CancellationToken.None);
        missingResult.Succeeded.Should().BeFalse();
        missingResult.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        // Act - tenant without connection string
        var noConnResult = await migrator.MigrateTenantAsync(tenantWithoutConnStr.Id, CancellationToken.None);
        noConnResult.Succeeded.Should().BeFalse();
        noConnResult.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }
}
