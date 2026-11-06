namespace Tests.Infrastructure;

using Common.MultiTenancy;
using ExamPrism.Domain.Identity;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
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
        entityType!.GetTableName().Should().Be("tenants");

        // Slug
        var slugProp = entityType.FindProperty(nameof(Tenant.Slug))!;
        slugProp.Should().NotBeNull();
        slugProp.GetColumnName().Should().Be("slug");
        slugProp.IsNullable.Should().BeFalse();
        slugProp.GetMaxLength().Should().Be(63);

        var slugIndex = entityType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(Tenant.Slug));
        slugIndex.Should().NotBeNull();
        slugIndex!.IsUnique.Should().BeTrue();
        slugIndex.GetDatabaseName().Should().Be("ix_tenants_slug");

        // Name
        var nameProp = entityType.FindProperty(nameof(Tenant.Name))!;
        nameProp.Should().NotBeNull();
        nameProp.GetColumnName().Should().Be("name");
        nameProp.IsNullable.Should().BeFalse();
        nameProp.GetMaxLength().Should().Be(200);

        // Status
        var statusProp = entityType.FindProperty(nameof(Tenant.Status))!;
        statusProp.Should().NotBeNull();
        statusProp.GetColumnName().Should().Be("status");
        var statusConverter = statusProp.GetValueConverter() ?? statusProp.FindTypeMapping()?.Converter;
        statusConverter.Should().NotBeNull();
        statusConverter!.ProviderClrType.Should().Be(typeof(string));
        statusProp.GetMaxLength().Should().Be(32);

        var statusIndex = entityType.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(Tenant.Status));
        statusIndex.Should().NotBeNull();
        statusIndex!.GetDatabaseName().Should().Be("ix_tenants_status");

        // EncryptedConnectionString
        var connProp = entityType.FindProperty(nameof(Tenant.EncryptedConnectionString))!;
        connProp.Should().NotBeNull();
        connProp.GetColumnName().Should().Be("encrypted_connection_string");
        connProp.IsNullable.Should().BeTrue();

        // FailureReason
        var reasonProp = entityType.FindProperty(nameof(Tenant.FailureReason))!;
        reasonProp.Should().NotBeNull();
        reasonProp.GetColumnName().Should().Be("failure_reason");
        reasonProp.IsNullable.Should().BeTrue();
        reasonProp.GetMaxLength().Should().Be(1000);

        // CreatedAt & UpdatedAt
        var createdProp = entityType.FindProperty(nameof(Tenant.CreatedAt))!;
        createdProp.Should().NotBeNull();
        createdProp.GetColumnName().Should().Be("created_at");
        createdProp.IsNullable.Should().BeFalse();
        createdProp.ClrType.Should().Be(typeof(DateTime));

        var provisionedProp = entityType.FindProperty(nameof(Tenant.ProvisionedAt))!;
        provisionedProp.Should().NotBeNull();
        provisionedProp.GetColumnName().Should().Be("provisioned_at");
        provisionedProp.IsNullable.Should().BeTrue();
        provisionedProp.ClrType.Should().Be(typeof(DateTime?));

        var updatedProp = entityType.FindProperty(nameof(Tenant.UpdatedAt))!;
        updatedProp.Should().NotBeNull();
        updatedProp.GetColumnName().Should().Be("updated_at");
        updatedProp.IsNullable.Should().BeFalse();
        updatedProp.ClrType.Should().Be(typeof(DateTime));

        // OwnerEmail & EncryptedOwnerPassword
        var ownerEmailProp = entityType.FindProperty(nameof(Tenant.OwnerEmail))!;
        ownerEmailProp.Should().NotBeNull();
        ownerEmailProp.GetColumnName().Should().Be("owner_email");
        ownerEmailProp.IsNullable.Should().BeTrue();
        ownerEmailProp.GetMaxLength().Should().Be(256);

        var encryptedOwnerPassProp = entityType.FindProperty(nameof(Tenant.EncryptedOwnerPassword))!;
        encryptedOwnerPassProp.Should().NotBeNull();
        encryptedOwnerPassProp.GetColumnName().Should().Be("encrypted_owner_password");
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
        userEntity!.GetTableName().Should().Be("users");
        userEntity.FindProperty(nameof(PlatformUser.UserName))!.GetColumnName().Should().Be("user_name");
        userEntity.FindProperty(nameof(PlatformUser.Email))!.GetColumnName().Should().Be("email");

        var roleEntity = context.Model.FindEntityType(typeof(PlatformRole));
        roleEntity.Should().NotBeNull();
        roleEntity!.GetTableName().Should().Be("roles");
        roleEntity.FindProperty(nameof(PlatformRole.Name))!.GetColumnName().Should().Be("name");

        var rtEntity = context.Model.FindEntityType(typeof(PlatformRefreshToken));
        rtEntity.Should().NotBeNull();
        rtEntity!.GetTableName().Should().Be("platform_refresh_tokens");

        var tokenHashProp = rtEntity.FindProperty(nameof(PlatformRefreshToken.TokenHash));
        tokenHashProp.Should().NotBeNull();
        tokenHashProp!.GetColumnName().Should().Be("token_hash");
        tokenHashProp.IsNullable.Should().BeFalse();
        tokenHashProp.GetMaxLength().Should().Be(128);

        var tokenHashIndex = rtEntity.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(PlatformRefreshToken.TokenHash));
        tokenHashIndex.Should().NotBeNull();
        tokenHashIndex!.IsUnique.Should().BeTrue();
        tokenHashIndex.GetDatabaseName().Should().Be("ix_platform_refresh_tokens_token_hash");

        var userIdIndex = rtEntity.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(PlatformRefreshToken.UserId));
        userIdIndex.Should().NotBeNull();
        userIdIndex!.GetDatabaseName().Should().Be("ix_platform_refresh_tokens_user_id");

        // Identity Auxiliary Tables
        var userRoleEntity = context.Model.FindEntityType(typeof(IdentityUserRole<Guid>));
        userRoleEntity.Should().NotBeNull();
        userRoleEntity!.GetTableName().Should().Be("user_roles");
        userRoleEntity.FindProperty("UserId")!.GetColumnName().Should().Be("user_id");
        userRoleEntity.FindProperty("RoleId")!.GetColumnName().Should().Be("role_id");

        var userClaimEntity = context.Model.FindEntityType(typeof(IdentityUserClaim<Guid>));
        userClaimEntity.Should().NotBeNull();
        userClaimEntity!.GetTableName().Should().Be("user_claims");
        userClaimEntity.FindProperty("Id")!.GetColumnName().Should().Be("id");
        userClaimEntity.FindProperty("UserId")!.GetColumnName().Should().Be("user_id");

        var userLoginEntity = context.Model.FindEntityType(typeof(IdentityUserLogin<Guid>));
        userLoginEntity.Should().NotBeNull();
        userLoginEntity!.GetTableName().Should().Be("user_logins");
        userLoginEntity.FindProperty("LoginProvider")!.GetColumnName().Should().Be("login_provider");

        var roleClaimEntity = context.Model.FindEntityType(typeof(IdentityRoleClaim<Guid>));
        roleClaimEntity.Should().NotBeNull();
        roleClaimEntity!.GetTableName().Should().Be("role_claims");
        roleClaimEntity.FindProperty("RoleId")!.GetColumnName().Should().Be("role_id");

        var userTokenEntity = context.Model.FindEntityType(typeof(IdentityUserToken<Guid>));
        userTokenEntity.Should().NotBeNull();
        userTokenEntity!.GetTableName().Should().Be("user_tokens");
        userTokenEntity.FindProperty("UserId")!.GetColumnName().Should().Be("user_id");
    }

    [Fact]
    public void AppDbContext_ModelMetadata_ConfiguresIdentityAndRefreshTokens_InSnakeCase()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=dummy_app")
            .Options;

        using var context = new AppDbContext(options);

        var userEntity = context.Model.FindEntityType(typeof(ApplicationUser));
        userEntity.Should().NotBeNull();
        userEntity!.GetTableName().Should().Be("users");
        userEntity.FindProperty(nameof(ApplicationUser.UserName))!.GetColumnName().Should().Be("user_name");
        userEntity.FindProperty(nameof(ApplicationUser.Email))!.GetColumnName().Should().Be("email");

        var rtEntity = context.Model.FindEntityType(typeof(RefreshToken));
        rtEntity.Should().NotBeNull();
        rtEntity!.GetTableName().Should().Be("refresh_tokens");
        rtEntity.FindProperty(nameof(RefreshToken.TokenHash))!.GetColumnName().Should().Be("token_hash");
        rtEntity.FindProperty(nameof(RefreshToken.UserId))!.GetColumnName().Should().Be("user_id");

        var tokenHashIndex = rtEntity.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(RefreshToken.TokenHash));
        tokenHashIndex.Should().NotBeNull();
        tokenHashIndex!.IsUnique.Should().BeTrue();
        tokenHashIndex.GetDatabaseName().Should().Be("ix_refresh_tokens_token_hash");
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
        catalogMigrations.Should().ContainSingle(m => m.EndsWith("_InitialCatalog"));

        tenantMigrations.Should().NotBeEmpty();
        tenantMigrations.Should().ContainSingle(m => m.EndsWith("_InitialTenant"));

        catalogMigrations.Intersect(tenantMigrations).Should().BeEmpty();
    }
}
