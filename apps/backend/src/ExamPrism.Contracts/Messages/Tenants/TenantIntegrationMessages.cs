namespace Common.Messages.Tenants;

using System;

public sealed record InitializeTenantIdentityCommand(Guid TenantId, string TenantSlug, string RawConnectionString, string? OwnerEmail, string? EncryptedOwnerPassword);
public sealed record InitializeTenantIdentityResult(bool Succeeded);
public sealed record MigrateTenantDatabaseCommand(Guid TenantId, string TenantSlug, string RawConnectionString);
public sealed record MigrateTenantDatabaseResult(int AppliedMigrationsCount);

