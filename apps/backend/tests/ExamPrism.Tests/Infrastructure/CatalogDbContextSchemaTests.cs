namespace Tests.Infrastructure;

using Common.MultiTenancy;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

public class CatalogDbContextSchemaTests
{
    [Theory]
    [InlineData("a", true)]
    [InlineData("school-a", true)]
    [InlineData("org123", true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789-abcdefghijklmnopqrstuvwxyz", true)] // 63 chars
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("School-A", false)]
    [InlineData("-abc", false)]
    [InlineData("abc-", false)]
    [InlineData("a.b", false)]
    [InlineData("a_b", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789-abcdefghijklmnopqrstuvwxyz0", false)] // 64 chars
    public void TenantSlugValidator_AcceptsValidDnsLabels_AndRejectsInvalidLabels(string? slug, bool expected)
    {
        var result = TenantSlugValidator.IsValid(slug);
        result.Should().Be(expected);
    }

    [Fact]
    public void CatalogDbContext_ModelMetadata_ConfiguresTenantsTableIndexesAndConversions()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=dummy")
            .Options;

        using var context = new CatalogDbContext(options);
        var entityType = context.Model.FindEntityType(typeof(Tenant));

        entityType.Should().NotBeNull();
        entityType!.GetTableName().Should().Be("Tenants");

        // Slug
        var slugProp = entityType.FindProperty(nameof(Tenant.Slug))!;
        slugProp.Should().NotBeNull();
        slugProp.IsNullable.Should().BeFalse();
        slugProp.GetMaxLength().Should().Be(63);

        var slugIndex = entityType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(Tenant.Slug));
        slugIndex.Should().NotBeNull();
        slugIndex!.IsUnique.Should().BeTrue();
        slugIndex.GetDatabaseName().Should().Be("IX_Tenants_Slug");

        // Name
        var nameProp = entityType.FindProperty(nameof(Tenant.Name))!;
        nameProp.Should().NotBeNull();
        nameProp.IsNullable.Should().BeFalse();
        nameProp.GetMaxLength().Should().Be(200);

        // Status
        var statusProp = entityType.FindProperty(nameof(Tenant.Status))!;
        statusProp.Should().NotBeNull();
        var statusConverter = statusProp.GetValueConverter() ?? statusProp.FindTypeMapping()?.Converter;
        statusConverter.Should().NotBeNull();
        statusConverter!.ProviderClrType.Should().Be(typeof(string));
        statusProp.GetMaxLength().Should().Be(32);

        var statusIndex = entityType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(Tenant.Status));
        statusIndex.Should().NotBeNull();
        statusIndex!.GetDatabaseName().Should().Be("IX_Tenants_Status");

        // EncryptedConnectionString
        var connProp = entityType.FindProperty(nameof(Tenant.EncryptedConnectionString))!;
        connProp.Should().NotBeNull();
        connProp.IsNullable.Should().BeTrue();

        // FailureReason
        var reasonProp = entityType.FindProperty(nameof(Tenant.FailureReason))!;
        reasonProp.Should().NotBeNull();
        reasonProp.IsNullable.Should().BeTrue();
        reasonProp.GetMaxLength().Should().Be(1000);

        // CreatedAt & UpdatedAt
        var createdProp = entityType.FindProperty(nameof(Tenant.CreatedAt))!;
        createdProp.Should().NotBeNull();
        createdProp.IsNullable.Should().BeFalse();

        var provisionedProp = entityType.FindProperty(nameof(Tenant.ProvisionedAt))!;
        provisionedProp.Should().NotBeNull();
        provisionedProp.IsNullable.Should().BeTrue();

        var updatedProp = entityType.FindProperty(nameof(Tenant.UpdatedAt))!;
        updatedProp.Should().NotBeNull();
        updatedProp.IsNullable.Should().BeFalse();

        // OwnerEmail & EncryptedOwnerPassword
        var ownerEmailProp = entityType.FindProperty(nameof(Tenant.OwnerEmail))!;
        ownerEmailProp.Should().NotBeNull();
        ownerEmailProp.IsNullable.Should().BeTrue();
        ownerEmailProp.GetMaxLength().Should().Be(256);

        var encryptedOwnerPassProp = entityType.FindProperty(nameof(Tenant.EncryptedOwnerPassword))!;
        encryptedOwnerPassProp.Should().NotBeNull();
        encryptedOwnerPassProp.IsNullable.Should().BeTrue();
    }

    [Fact]
    public void CatalogDbContext_ModelMetadata_ConfiguresPlatformIdentityAndPlatformRefreshTokens()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=dummy_catalog")
            .Options;

        using var context = new CatalogDbContext(options);

        var userEntity = context.Model.FindEntityType(typeof(PlatformUser));
        userEntity.Should().NotBeNull();

        var roleEntity = context.Model.FindEntityType(typeof(PlatformRole));
        roleEntity.Should().NotBeNull();

        var rtEntity = context.Model.FindEntityType(typeof(PlatformRefreshToken));
        rtEntity.Should().NotBeNull();
        rtEntity!.GetTableName().Should().Be("PlatformRefreshTokens");

        var tokenHashProp = rtEntity.FindProperty(nameof(PlatformRefreshToken.TokenHash));
        tokenHashProp.Should().NotBeNull();
        tokenHashProp!.IsNullable.Should().BeFalse();
        tokenHashProp.GetMaxLength().Should().Be(128);

        var tokenHashIndex = rtEntity.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(PlatformRefreshToken.TokenHash));
        tokenHashIndex.Should().NotBeNull();
        tokenHashIndex!.IsUnique.Should().BeTrue();
        tokenHashIndex.GetDatabaseName().Should().Be("IX_PlatformRefreshTokens_TokenHash");

        var userIdIndex = rtEntity.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(PlatformRefreshToken.UserId));
        userIdIndex.Should().NotBeNull();
        userIdIndex!.GetDatabaseName().Should().Be("IX_PlatformRefreshTokens_UserId");
    }

    [Fact]
    public void CatalogAndTenantMigrations_ExistSeparately_AndDoNotCrossContaminate()
    {
        var catalogOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=dummy_catalog")
            .Options;
        using var catalogContext = new CatalogDbContext(catalogOptions);

        var appOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=dummy_tenant")
            .Options;
        using var appContext = new AppDbContext(appOptions);

        var catalogAssembly = catalogContext.GetService<IMigrationsAssembly>();
        var appAssembly = appContext.GetService<IMigrationsAssembly>();

        var catalogMigrations = catalogAssembly.Migrations.Keys.ToList();
        var tenantMigrations = appAssembly.Migrations.Keys.ToList();

        catalogMigrations.Should().NotBeEmpty();
        catalogMigrations.Should().Contain(m => m.EndsWith("_InitialCatalog"));
        catalogMigrations.Should().Contain(m => m.EndsWith("_AddPlatformIdentity"));
        catalogMigrations.Should().Contain(m => m.EndsWith("_AddTenantProvisioningMetadata"));

        tenantMigrations.Should().NotBeEmpty();
        tenantMigrations.Should().ContainSingle(m => m.EndsWith("_InitialTenant"));

        catalogMigrations.Intersect(tenantMigrations).Should().BeEmpty();
    }
}
