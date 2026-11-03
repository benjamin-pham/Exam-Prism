namespace ExamPrism.Infrastructure.MultiTenancy;

using System.Security.Cryptography;
using Common.MultiTenancy;
using Common.Security;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

public sealed class TenantProvisioner(
    CatalogDbContext catalogDb,
    IConfiguration configuration,
    IOptions<MultiTenancyOptions> options,
    IConnectionStringProtector protector,
    ITenantConnectionStringNormalizer normalizer,
    ITenantCacheInvalidator cacheInvalidator,
    IServiceScopeFactory serviceScopeFactory,
    TimeProvider timeProvider,
    ILogger<TenantProvisioner> logger) : ITenantProvisioner
{
    public async Task ProvisionAsync(Guid tenantId, CancellationToken ct = default)
    {
        Tenant? tenant = null;
        try
        {
            tenant = await catalogDb.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            if (tenant is null || tenant.Status is TenantStatus.Active or TenantStatus.Suspended)
            {
                return;
            }

            if (!TenantSlugValidator.IsValid(tenant.Slug))
            {
                throw new InvalidOperationException($"Invalid tenant slug: '{tenant.Slug}'.");
            }

            var provisioningOptions = options.Value.Provisioning;
            var safeSuffix = tenant.Slug.Replace('-', '_');
            var dbName = $"{provisioningOptions.DatabaseNamePrefix}{safeSuffix}";
            if (dbName.Length > 63)
            {
                dbName = dbName[..63];
            }

            var baseConnStr = !string.IsNullOrWhiteSpace(provisioningOptions.ServerConnectionString)
                ? provisioningOptions.ServerConnectionString
                : configuration.GetConnectionString("CatalogConnection")
                    ?? throw new InvalidOperationException("Catalog connection string missing.");

            var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnStr)
            {
                Database = "postgres"
            };

            string rawTenantConnStr;
            string? roleName = null;
            string? rolePassword = null;

            if (provisioningOptions.CreateDedicatedPostgresUser)
            {
                roleName = $"{provisioningOptions.DedicatedUserPrefix}{safeSuffix}";
                if (roleName.Length > 63)
                {
                    roleName = roleName[..63];
                }

                if (!string.IsNullOrWhiteSpace(tenant.EncryptedConnectionString))
                {
                    var existingRaw = protector.Unprotect(tenant.EncryptedConnectionString);
                    var existingBuilder = new NpgsqlConnectionStringBuilder(existingRaw);
                    roleName = existingBuilder.Username;
                    rolePassword = existingBuilder.Password;
                    rawTenantConnStr = existingRaw;
                }
                else
                {
                    rolePassword = GenerateSecurePassword(32);
                    var tenantConnBuilder = new NpgsqlConnectionStringBuilder(baseConnStr)
                    {
                        Database = dbName,
                        Username = roleName,
                        Password = rolePassword
                    };
                    rawTenantConnStr = tenantConnBuilder.ConnectionString;
                    tenant.EncryptedConnectionString = protector.Protect(rawTenantConnStr);
                    await catalogDb.SaveChangesAsync(ct);
                }
            }
            else
            {
                var tenantConnBuilder = new NpgsqlConnectionStringBuilder(baseConnStr)
                {
                    Database = dbName
                };
                rawTenantConnStr = tenantConnBuilder.ConnectionString;
            }

            await using (var adminConn = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await adminConn.OpenAsync(ct);

                if (provisioningOptions.CreateDedicatedPostgresUser && roleName != null && rolePassword != null)
                {
                    await using var checkRoleCmd = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @rolname;", adminConn);
                    checkRoleCmd.Parameters.AddWithValue("rolname", roleName);
                    var roleExists = await checkRoleCmd.ExecuteScalarAsync(ct) != null;

                    var escapedPassword = rolePassword.Replace("'", "''");
                    if (!roleExists)
                    {
                        await using var createRoleCmd = new NpgsqlCommand($"CREATE ROLE \"{roleName}\" WITH LOGIN PASSWORD '{escapedPassword}';", adminConn);
                        await createRoleCmd.ExecuteNonQueryAsync(ct);
                    }
                    else
                    {
                        await using var alterRoleCmd = new NpgsqlCommand($"ALTER ROLE \"{roleName}\" WITH LOGIN PASSWORD '{escapedPassword}';", adminConn);
                        await alterRoleCmd.ExecuteNonQueryAsync(ct);
                    }
                }

                await using var checkDbCmd = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @datname;", adminConn);
                checkDbCmd.Parameters.AddWithValue("datname", dbName);
                var dbExists = await checkDbCmd.ExecuteScalarAsync(ct) != null;

                if (!dbExists)
                {
                    var createSql = provisioningOptions.CreateDedicatedPostgresUser && roleName != null
                        ? $"CREATE DATABASE \"{dbName}\" OWNER \"{roleName}\";"
                        : $"CREATE DATABASE \"{dbName}\";";

                    await using var createDbCmd = new NpgsqlCommand(createSql, adminConn);
                    await createDbCmd.ExecuteNonQueryAsync(ct);
                }
                else if (provisioningOptions.CreateDedicatedPostgresUser && roleName != null)
                {
                    // Review Focus #2: if database already existed from a prior failed attempt, run ALTER DATABASE "{dbName}" OWNER TO "{roleName}";
                    await using var alterDbCmd = new NpgsqlCommand($"ALTER DATABASE \"{dbName}\" OWNER TO \"{roleName}\";", adminConn);
                    await alterDbCmd.ExecuteNonQueryAsync(ct);
                }
            }

            var normalizedTenantConnStr = normalizer.Normalize(rawTenantConnStr);
            var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(normalizedTenantConnStr)
                .Options;

            await using (var tenantDb = new AppDbContext(dbOptions))
            {
                await tenantDb.Database.MigrateAsync(ct);
            }

            await using (var scope = serviceScopeFactory.CreateAsyncScope())
            {
                var setter = scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>();
                setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(
                    new ExamPrismTenantInfo(tenant.Id.ToString(), tenant.Slug)
                    {
                        Status = TenantStatus.Active,
                        ConnectionString = rawTenantConnStr
                    });

                var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
                var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

                if (!await roleManager.RoleExistsAsync(TenantRoles.TenantOwner))
                {
                    var roleResult = await roleManager.CreateAsync(new IdentityRole<Guid>(TenantRoles.TenantOwner));
                    if (!roleResult.Succeeded)
                    {
                        throw new InvalidOperationException(
                            $"Failed to create role '{TenantRoles.TenantOwner}': {string.Join(", ", roleResult.Errors.Select(e => e.Description))}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(tenant.OwnerEmail))
                {
                    var ownerUser = await userManager.FindByEmailAsync(tenant.OwnerEmail);
                    if (ownerUser is null)
                    {
                        if (string.IsNullOrWhiteSpace(tenant.EncryptedOwnerPassword))
                        {
                            throw new InvalidOperationException($"Owner password for tenant '{tenant.Slug}' is missing.");
                        }

                        var ownerPassword = protector.Unprotect(tenant.EncryptedOwnerPassword);
                        ownerUser = new ApplicationUser
                        {
                            Id = Guid.NewGuid(),
                            UserName = tenant.OwnerEmail,
                            Email = tenant.OwnerEmail,
                            EmailConfirmed = true
                        };

                        var createResult = await userManager.CreateAsync(ownerUser, ownerPassword);
                        if (!createResult.Succeeded)
                        {
                            throw new InvalidOperationException(
                                $"Failed to create tenant owner: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");
                        }
                    }

                    if (!await userManager.IsInRoleAsync(ownerUser, TenantRoles.TenantOwner))
                    {
                        var addRoleResult = await userManager.AddToRoleAsync(ownerUser, TenantRoles.TenantOwner);
                        if (!addRoleResult.Succeeded)
                        {
                            throw new InvalidOperationException(
                                $"Failed to add tenant owner to role '{TenantRoles.TenantOwner}': {string.Join(", ", addRoleResult.Errors.Select(e => e.Description))}");
                        }
                    }
                }
            }

            var now = timeProvider.GetUtcNow();
            tenant.EncryptedConnectionString = protector.Protect(rawTenantConnStr);
            tenant.Status = TenantStatus.Active;
            tenant.FailureReason = null;
            tenant.EncryptedOwnerPassword = null;
            tenant.ProvisionedAt = now;
            tenant.UpdatedAt = now;

            await catalogDb.SaveChangesAsync(ct);
            await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision tenant {TenantId} ({Slug})", tenantId, tenant?.Slug);

            if (tenant != null)
            {
                var now = timeProvider.GetUtcNow();
                tenant.Status = TenantStatus.Failed;
                tenant.FailureReason = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                tenant.UpdatedAt = now;

                try
                {
                    await catalogDb.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception saveEx)
                {
                    logger.LogError(saveEx, "Failed to save failed status for tenant {TenantId}", tenantId);
                }

                try
                {
                    await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, CancellationToken.None);
                }
                catch (Exception cacheEx)
                {
                    logger.LogError(cacheEx, "Failed to invalidate cache for failed tenant {TenantId}", tenantId);
                }
            }
        }
    }

    private static string GenerateSecurePassword(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return RandomNumberGenerator.GetString(chars, length);
    }
}
