namespace ExamPrism.Infrastructure.MultiTenancy;

using System.Security.Cryptography;
using Common.Messages.Tenants;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Domain.Tenants;
using ExamPrism.Domain.Tenants.Specifications;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Wolverine;

public sealed class TenantProvisioner(
    CatalogDbContext catalogDb,
    IConfiguration configuration,
    IOptions<MultiTenancyOptions> options,
    IConnectionStringProtector protector,
    ITenantCacheInvalidator cacheInvalidator,
    IServiceScopeFactory? serviceScopeFactory = null,
    TimeProvider? timeProvider = null,
    ILogger<TenantProvisioner>? logger = null,
    IMessageBus? bus = null) : ITenantProvisioner
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<TenantProvisioner> _logger = logger ?? NullLogger<TenantProvisioner>.Instance;

    public TenantProvisioner(
        CatalogDbContext catalogDb,
        IConfiguration configuration,
        IOptions<MultiTenancyOptions> options,
        IConnectionStringProtector protector,
        ITenantConnectionStringNormalizer normalizer,
        ITenantCacheInvalidator cacheInvalidator,
        IServiceScopeFactory? serviceScopeFactory = null,
        TimeProvider? timeProvider = null,
        ILogger<TenantProvisioner>? logger = null,
        IMessageBus? bus = null)
        : this(catalogDb, configuration, options, protector, cacheInvalidator, serviceScopeFactory, timeProvider, logger, bus)
    {
        _ = normalizer;
    }

    public TenantProvisioner(
        CatalogDbContext catalogDb,
        IConfiguration configuration,
        IOptions<MultiTenancyOptions> options,
        IConnectionStringProtector protector,
        ITenantConnectionStringNormalizer normalizer,
        ITenantCacheInvalidator cacheInvalidator,
        IMessageBus bus,
        TimeProvider? timeProvider = null,
        ILogger<TenantProvisioner>? logger = null)
        : this(catalogDb, configuration, options, protector, cacheInvalidator, null, timeProvider, logger, bus)
    {
        _ = normalizer;
    }

    public async Task ProvisionAsync(Guid tenantId, CancellationToken ct = default)
    {
        Tenant? tenant = null;
        try
        {
            tenant = await catalogDb.QueryFirstOrDefaultAsync(new TenantByIdSpec(tenantId, track: true), ct);
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
                    tenant.SetEncryptedConnectionString(protector.Protect(rawTenantConnStr), _timeProvider.GetUtcNow().UtcDateTime);
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
                    await using var alterDbCmd = new NpgsqlCommand($"ALTER DATABASE \"{dbName}\" OWNER TO \"{roleName}\";", adminConn);
                    await alterDbCmd.ExecuteNonQueryAsync(ct);
                }
            }

            AsyncServiceScope? fallbackScope = null;
            IMessageBus messageBus;
            if (bus is not null)
            {
                messageBus = bus;
            }
            else if (serviceScopeFactory is not null)
            {
                fallbackScope = serviceScopeFactory.CreateAsyncScope();
                messageBus = fallbackScope.Value.ServiceProvider.GetService<IMessageBus>()
                    ?? throw new InvalidOperationException("IMessageBus is required to initialize tenant identity.");
            }
            else
            {
                throw new InvalidOperationException("IMessageBus is required to initialize tenant identity.");
            }

            try
            {
                var initCommand = new InitializeTenantIdentityCommand(
                    tenant.Id,
                    tenant.Slug,
                    rawTenantConnStr,
                    tenant.OwnerEmail,
                    tenant.EncryptedOwnerPassword);
                var initResult = await messageBus.InvokeAsync<Result<InitializeTenantIdentityResult>>(initCommand, ct);
                if (initResult is null || initResult.IsFailure)
                {
                    throw new InvalidOperationException(initResult?.Error.Message ?? "Identity initialization returned null result.");
                }
            }
            finally
            {
                if (fallbackScope.HasValue)
                {
                    await fallbackScope.Value.DisposeAsync();
                }
            }

            var now = _timeProvider.GetUtcNow().UtcDateTime;
            tenant.MarkProvisioned(protector.Protect(rawTenantConnStr), now);

            await catalogDb.SaveChangesAsync(ct);
            await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision tenant {TenantId} ({Slug})", tenantId, tenant?.Slug);

            if (tenant != null)
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                tenant.MarkProvisioningFailed(ex.Message, now);

                try
                {
                    await catalogDb.SaveChangesAsync(CancellationToken.None);
                }
                catch (Exception saveEx)
                {
                    _logger.LogError(saveEx, "Failed to save failed status for tenant {TenantId}", tenantId);
                }

                try
                {
                    await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, CancellationToken.None);
                }
                catch (Exception cacheEx)
                {
                    _logger.LogError(cacheEx, "Failed to invalidate cache for failed tenant {TenantId}", tenantId);
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
