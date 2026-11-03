# Design Specification: Async Self-Service Provisioning & Fleet Migration Pipeline

**Date:** 2026-11-03  
**Status:** Draft (Pending User Spec Review)  
**Scope:** Architectural Subsystem — Sub-project 3 of 3  

---

## 1. Context, Goals, and Scope

ExamPrism has completed the first two sub-projects of its Multi-Tenant SaaS architecture:
* **Sub-project 1 — Multi-Tenant Core Foundation** (`docs/superpowers/specs/2026-11-02-multitenant-core-foundation-design.md`): Subdomain tenant resolution (`SubdomainParser`, `SubdomainMultiTenantStrategy`), ciphertext-cached catalog store (`CatalogMultiTenantStore` + `ITenantCacheInvalidator`), `CatalogDbContext` (`Tenants`), dynamic per-tenant `AppDbContext` with normalized Npgsql connection pooling, `TenantAccessGuardMiddleware`, `.RequireTenant()` / `.RequirePlatform()`, and cross-cutting isolation across JWTs, Host-Only cookies, `HybridCacheService`, `RateLimitingSetup`, and `TenantWolverineMiddleware`.
* **Sub-project 2 — Platform Identity & Admin Tenant Management** (`docs/superpowers/specs/2026-11-03-platform-identity-admin-tenant-management-design.md`): Dual ASP.NET Core Identity (`PlatformUser`, `PlatformRole`, `PlatformRefreshToken` in `CatalogDbContext`), `PlatformAdminSeeder`, two-way JWT scope isolation (`token_scope = "platform"` vs. `tenant_id`), Platform Auth APIs (`/api/platform/auth/*`), and Admin Tenant Management APIs (`/api/platform/tenants/*`).

**Sub-project 3 (This Specification)** completes the multi-tenant lifecycle by delivering **Async Self-Service Provisioning & Fleet Migration Pipeline**:
1. **Public Self-Service & Platform Admin Tenant Onboarding**:
   * Public slug availability check (`GET /api/tenants/check-slug/{slug}`), public self-service onboarding (`POST /api/tenants` returning `202 Accepted`), and public provisioning status polling (`GET /api/tenants/{slug}/status`).
   * Platform Admin tenant creation (`POST /api/platform/tenants`) and failed-provisioning retry (`POST /api/platform/tenants/{id:guid}/retry-provisioning`).
2. **Idempotent Background Wolverine Provisioning Worker (`ITenantProvisioner`)**:
   * Executes asynchronously via Wolverine local queue (`StartTenantProvisioningCommand(Guid TenantId)`).
   * Supports both shared PostgreSQL server credentials and optional per-tenant dedicated PostgreSQL user/role creation (`CreateDedicatedPostgresUser`).
   * Dynamically creates the physical PostgreSQL database (`CREATE DATABASE "examprism_tenant_{slug_underscored}"`), runs EF Core tenant schema migrations (`AppDbContext.Database.MigrateAsync()`), seeds the `TenantOwner` role and initial Owner `ApplicationUser`, encrypts and stores the tenant connection string, wipes the temporary encrypted owner password (`EncryptedOwnerPassword = null`), transitions the tenant to `Active`, and invalidates catalog/tenant caches.
   * Recovers any stuck `Provisioning` tenants on application startup (`RecoverStuckProvisioningOnStartup`).
3. **Controlled Fleet Schema Migration Pipeline (`ITenantFleetMigrator`)**:
   * Bounded-parallelism migration runner (`Parallel.ForEachAsync` with `MaxDegreeOfParallelism`) capable of migrating >500 physical tenant databases without exhausting connections or halting on single-tenant errors.
   * Triggerable both on-demand by Platform Admins (`POST /api/platform/tenants/migrate` and `POST /api/platform/tenants/{id:guid}/migrate`) and optionally on application startup (`MigrateOnStartup`).

---

## 2. Persistence & Configuration Schema

### 2.1. `Tenant` Entity & `CatalogDbContext` Upgrade
Extend `Tenant` (`ExamPrism.Infrastructure.Persistence.Catalog.Tenant`) with two nullable onboarding properties:
```csharp
public class Tenant
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? EncryptedConnectionString { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Provisioning;
    public string? FailureReason { get; set; }
    public string? OwnerEmail { get; set; }
    public string? EncryptedOwnerPassword { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProvisionedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```
* **`CatalogDbContext` Configuration**:
  * `OwnerEmail`: `HasMaxLength(256)`, `IsRequired(false)`.
  * `EncryptedOwnerPassword`: `IsRequired(false)`.Encrypted using `IConnectionStringProtector` while `Status` is `Provisioning` or `Failed`, and explicitly cleared (`null`) as soon as `Status` transitions to `Active`.
* **Catalog EF Core Migration**:
  * Generate migration `AddTenantProvisioningMetadata` under `Infrastructure/Persistence/Migrations/Catalog` (`--context CatalogDbContext`).

### 2.2. Tenant Role Constants (`Common.Security.TenantRoles`)
```csharp
namespace Common.Security;

public static class TenantRoles
{
    public const string TenantOwner = "TenantOwner";
}
```

### 2.3. Configuration Schema (`MultiTenancyOptions`)
Extend `MultiTenancyOptions` (`ExamPrism.Infrastructure.MultiTenancy`) with `Provisioning` and `FleetMigration` sections:
```csharp
public sealed class MultiTenancyOptions
{
    public const string SectionName = "MultiTenancy";

    public string[] BaseDomains { get; set; } = ["localhost"];
    public string[] ReservedSubdomains { get; set; } = ["www", "admin", "api"];
    public bool AllowHeaderFallbackInDevelopment { get; set; } = true;
    public string HeaderName { get; set; } = "X-Tenant";
    public int CatalogCacheExpirationMinutes { get; set; } = 30;
    public int CatalogLocalCacheExpirationMinutes { get; set; } = 5;
    public int NotFoundCacheExpirationSeconds { get; set; } = 30;
    public int ProvisioningRetryAfterSeconds { get; set; } = 5;
    public TenantDatabasePoolOptions DatabasePool { get; set; } = new();
    public TenantProvisioningOptions Provisioning { get; set; } = new();
    public TenantFleetMigrationOptions FleetMigration { get; set; } = new();
}

public sealed class TenantProvisioningOptions
{
    public string? ServerConnectionString { get; set; }
    public string DatabaseNamePrefix { get; set; } = "examprism_tenant_";
    public string DedicatedUserPrefix { get; set; } = "examprism_u_";
    public bool CreateDedicatedPostgresUser { get; set; } = false;
    public bool RecoverStuckProvisioningOnStartup { get; set; } = true;
}

public sealed class TenantFleetMigrationOptions
{
    public bool MigrateOnStartup { get; set; } = false;
    public int MaxDegreeOfParallelism { get; set; } = 10;
}
```
* In `appsettings.json`:
  ```json
  "Provisioning": {
    "ServerConnectionString": "",
    "DatabaseNamePrefix": "examprism_tenant_",
    "DedicatedUserPrefix": "examprism_u_",
    "CreateDedicatedPostgresUser": false,
    "RecoverStuckProvisioningOnStartup": true
  },
  "FleetMigration": {
    "MigrateOnStartup": false,
    "MaxDegreeOfParallelism": 10
  }
  ```
* In `appsettings.Development.json`:
  * Set `"MultiTenancy:FleetMigration:MigrateOnStartup": true`.

---

## 3. Public & Admin Onboarding Vertical Slices

### 3.1. `GET /api/tenants/check-slug/{slug}`
* **Endpoint**: `CheckTenantSlugEndpoint` (`ExamPrism.Endpoints.Tenants`)
  * Route: `GET /api/tenants/check-slug/{slug}`
  * Metadata: `.WithTags("Tenant Onboarding")`, `.RequirePlatform()`, `.RequireRateLimiting(RateLimitPolicies.Auth)`.
* **Contract & Handler (`CheckTenantSlugQuery` / `CheckTenantSlugHandler` in `ExamPrism.Features.Tenants`)**:
  * `CheckTenantSlugQuery(string Slug)` -> `CheckTenantSlugResult(string Slug, bool IsAvailable, string? ReasonCode)`
  * Normalizes `slug = (query.Slug ?? string.Empty).Trim().ToLowerInvariant()`.
  * Checks:
    1. `!TenantSlugValidator.IsValid(slug)` -> returns `CheckTenantSlugResult(slug, false, "INVALID_SLUG")`.
    2. `options.Value.ReservedSubdomains.Contains(slug, StringComparer.OrdinalIgnoreCase)` -> returns `CheckTenantSlugResult(slug, false, "RESERVED_SLUG")`.
    3. `await db.Tenants.AsNoTracking().AnyAsync(t => t.Slug == slug, ct)` -> if `true`, returns `CheckTenantSlugResult(slug, false, "SLUG_ALREADY_TAKEN")`.
    4. Otherwise returns `CheckTenantSlugResult(slug, true, null)`.

### 3.2. `POST /api/tenants` (Public Self-Service) & `POST /api/platform/tenants` (Admin)
Both endpoints invoke the shared `CreateTenantCommand` / `CreateTenantHandler` (`ExamPrism.Features.Tenants`):
* **Endpoints**:
  1. `ProvisionTenantPublicEndpoint` (`POST /api/tenants`): `.WithTags("Tenant Onboarding")`, `.RequirePlatform()`, `.RequireRateLimiting(RateLimitPolicies.Auth)`.
  2. `CreatePlatformTenantEndpoint` (`POST /api/platform/tenants`): `.WithTags("Platform Tenants")`, `.RequirePlatform()`, `.RequireAuthorization(PlatformRoles.PolicyName)`.
  * Both return `Results.Accepted($"/api/tenants/{result.Value.Slug}/status", result.Value)` (`202 Accepted`) on success.
* **Contract & Validator**:
  ```csharp
  public record CreateTenantCommand(
      [Required] string Slug,
      [Required] string Name,
      [Required, EmailAddress] string OwnerEmail,
      [Required, MinLength(6)] string OwnerPassword);
  ```
  * `CreateTenantCommandValidator : AbstractValidator<CreateTenantCommand>`:
    * `Slug`: `NotEmpty().WithErrorCode("REQUIRED")`, `MaximumLength(63).WithErrorCode("MAX_LENGTH")`, `.Must(s => TenantSlugValidator.IsValid(s?.Trim().ToLowerInvariant())).WithErrorCode("INVALID_SLUG")`.
    * `Name`: `NotEmpty().WithErrorCode("REQUIRED")`, `MaximumLength(200).WithErrorCode("MAX_LENGTH")`.
    * `OwnerEmail`: `NotEmpty().WithErrorCode("REQUIRED")`, `EmailAddress().WithErrorCode("INVALID_EMAIL")`, `MaximumLength(256).WithErrorCode("MAX_LENGTH")`.
    * `OwnerPassword`: `NotEmpty().WithErrorCode("REQUIRED")`, `MinimumLength(6).WithErrorCode("PASSWORD_TOO_SHORT")` (matching ASP.NET Core Identity password rules configured in `DependencyInjection.cs`: min length 6, requires digit, lowercase, uppercase, non-alphanumeric).
* **Response**:
  ```csharp
  public record TenantProvisioningAcceptedResult(
      Guid Id,
      string Slug,
      string Name,
      TenantStatus Status,
      DateTimeOffset CreatedAt);
  ```
* **Handler (`CreateTenantHandler`)**:
  1. Normalizes `slug = command.Slug.Trim().ToLowerInvariant()`, `name = command.Name.Trim()`, `ownerEmail = command.OwnerEmail.Trim()`.
  2. Validates `slug` against `MultiTenancyOptions.ReservedSubdomains`; if reserved or already exists in `CatalogDbContext.Tenants`, returns `Error.Conflict("SLUG_ALREADY_TAKEN", "This organization subdomain is already taken or reserved.")`.
  3. Validates `command.OwnerPassword` against registered `IPasswordValidator<ApplicationUser>` (or explicit rule checks) before creating the tenant record so invalid passwords fail fast with `400 Bad Request` rather than failing asynchronously in the background worker.
  4. Creates `Tenant` entity:
     * `Id = Guid.NewGuid()`
     * `Slug = slug`
     * `Name = name`
     * `Status = TenantStatus.Provisioning`
     * `OwnerEmail = ownerEmail`
     * `EncryptedOwnerPassword = protector.Protect(command.OwnerPassword)`
     * `CreatedAt = now`, `UpdatedAt = now`
  5. Saves to `CatalogDbContext` (catching unique index violation on `IX_Tenants_Slug` to return `Error.Conflict("SLUG_ALREADY_TAKEN", "This organization subdomain is already taken or reserved.")`).
  6. Calls `await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct)` (clearing any prior negative cache entry for `slug`).
  7. Enqueues background provisioning via `await bus.SendAsync(new StartTenantProvisioningCommand(tenant.Id))`.
  8. Returns `TenantProvisioningAcceptedResult`.

### 3.3. `GET /api/tenants/{slug}/status` (Public Status Polling)
* **Endpoint**: `GetTenantProvisioningStatusEndpoint` (`ExamPrism.Endpoints.Tenants`)
  * Route: `GET /api/tenants/{slug}/status`
  * Metadata: `.WithTags("Tenant Onboarding")`, `.RequirePlatform()`.
* **Contract & Handler (`GetTenantProvisioningStatusQuery` / `GetTenantProvisioningStatusHandler`)**:
  * `GetTenantProvisioningStatusResult(string Slug, string Name, TenantStatus Status, DateTimeOffset? ProvisionedAt)`
  * Queries `CatalogDbContext.Tenants.AsNoTracking()` by normalized `slug`.
  * If `null`, returns `Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.")`.
  * Never exposes `FailureReason`, `OwnerEmail`, or `EncryptedConnectionString`.

### 3.4. `POST /api/platform/tenants/{id:guid}/retry-provisioning` (Admin Retry)
* **Endpoint**: `RetryTenantProvisioningEndpoint` (`ExamPrism.Endpoints.Platform.Tenants`)
  * Route: `POST /api/platform/tenants/{id:guid}/retry-provisioning`
  * Metadata: `.WithTags("Platform Tenants")`, `.RequirePlatform()`, `.RequireAuthorization(PlatformRoles.PolicyName)`.
  * Returns `Results.Accepted($"/api/platform/tenants/{id}", result.Value)` (`202 Accepted`) on success.
* **Contract & Handler (`RetryTenantProvisioningCommand(Guid Id)` / `RetryTenantProvisioningHandler`)**:
  * Loads `Tenant` from `CatalogDbContext.Tenants`. If `null`, returns `Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.")`.
  * State guard: Only a tenant in `TenantStatus.Failed` or `TenantStatus.Provisioning` can retry provisioning. If `tenant.Status is TenantStatus.Active or TenantStatus.Suspended`, returns `Error.Conflict("INVALID_TENANT_STATUS_TRANSITION", "Only a failed or provisioning organization workspace can retry provisioning.")`.
  * Sets `tenant.Status = TenantStatus.Provisioning`, `tenant.FailureReason = null`, `tenant.UpdatedAt = timeProvider.GetUtcNow()`.
  * Saves changes, calls `await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct)`, and enqueues `await bus.SendAsync(new StartTenantProvisioningCommand(tenant.Id))`.
  * Returns `Result<TenantDetailResult>`.

---

## 4. Background Provisioning Worker & Startup Recovery

### 4.1. `ITenantProvisioner` & `StartTenantProvisioningHandler`
* **`Common.MultiTenancy.ITenantProvisioner`**:
  ```csharp
  namespace Common.MultiTenancy;

  public interface ITenantProvisioner
  {
      Task ProvisionAsync(Guid tenantId, CancellationToken ct = default);
  }
  ```
* **`StartTenantProvisioningCommand(Guid TenantId)` & `StartTenantProvisioningHandler`** (`ExamPrism.Features.Tenants`):
  * Implements `IHandler<StartTenantProvisioningCommand>`.
  * Calls `await provisioner.ProvisionAsync(command.TenantId, ct)` and returns `Result.Success()`.

### 4.2. `TenantProvisioner` Execution Pipeline (`ExamPrism.Infrastructure.MultiTenancy`)
`TenantProvisioner.ProvisionAsync(Guid tenantId, CancellationToken ct)` is 100% idempotent and safe across retries:
1. **Load & Guard**:
   * Fetch `Tenant` from `CatalogDbContext.Tenants`.
   * If `tenant is null` or `tenant.Status is TenantStatus.Active or TenantStatus.Suspended`, return immediately.
2. **Identifier Validation & Database Name Construction**:
   * Re-verify `TenantSlugValidator.IsValid(tenant.Slug)` (defense-in-depth).
   * Convert hyphens to underscores: `safeSuffix = tenant.Slug.Replace('-', '_')` (strictly `[a-z0-9_]+`).
   * `dbName = $"{provisioningOptions.DatabaseNamePrefix}{safeSuffix}"` (truncated to 63 chars if needed).
3. **Resolve Server Admin Connection**:
   * Base connection string = `!string.IsNullOrWhiteSpace(provisioningOptions.ServerConnectionString) ? provisioningOptions.ServerConnectionString : catalogConnectionString`.
   * Build `adminConnBuilder = new NpgsqlConnectionStringBuilder(baseConnString) { Database = "postgres" }`.
4. **Provision PostgreSQL Role (When `CreateDedicatedPostgresUser == true`) & Database**:
   * Open `NpgsqlConnection` using `adminConnBuilder.ConnectionString`.
   * Determine target tenant connection string (`rawTenantConnString`):
     * If `CreateDedicatedPostgresUser == true`:
       * If `!string.IsNullOrWhiteSpace(tenant.EncryptedConnectionString)`, decrypt existing credentials via `protector.Unprotect(tenant.EncryptedConnectionString)` and extract `Username`/`Password`.
       * Otherwise, generate `roleName = $"{provisioningOptions.DedicatedUserPrefix}{safeSuffix}"` and a cryptographically random 32-char alphanumeric password, build `rawTenantConnString` pointing to `Database = dbName` with `Username = roleName, Password = rolePassword`, and save `tenant.EncryptedConnectionString = protector.Protect(rawTenantConnString)` to `CatalogDbContext` before DDL execution so retries reuse the same credentials.
       * Ensure PostgreSQL role exists (`SELECT 1 FROM pg_roles WHERE rolname = @roleName`): if missing, execute `CREATE ROLE "{roleName}" WITH LOGIN PASSWORD '{escapedPassword}';`, else `ALTER ROLE "{roleName}" WITH LOGIN PASSWORD '{escapedPassword}';`.
     * If `CreateDedicatedPostgresUser == false`:
       * Build `rawTenantConnString` from `baseConnString` with `Database = dbName`.
   * Check database existence (`SELECT 1 FROM pg_database WHERE datname = @dbName`):
     * If missing:
       * When `CreateDedicatedPostgresUser == true`: execute `CREATE DATABASE "{dbName}" OWNER "{roleName}";`.
       * When `CreateDedicatedPostgresUser == false`: execute `CREATE DATABASE "{dbName}";`.
5. **Run Tenant EF Core Migrations (`AppDbContext`)**:
   * Normalize `rawTenantConnString` via `ITenantConnectionStringNormalizer.Normalize(rawTenantConnString)`.
   * Create `AppDbContext` with `new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(normalizedConnString).Options`.
   * Execute `await tenantDb.Database.MigrateAsync(ct)`.
6. **Seed `TenantOwner` Role & Initial Owner Account (Idempotent)**:
   * Create an async DI scope (`serviceScopeFactory.CreateAsyncScope()`).
   * Set `IMultiTenantContextSetter.MultiTenantContext` inside that scope to an active `ExamPrismTenantInfo` with `ConnectionString = rawTenantConnString`.
   * Resolve `RoleManager<IdentityRole<Guid>>` and `UserManager<ApplicationUser>`:
     * Ensure role `TenantRoles.TenantOwner` exists.
     * If `!string.IsNullOrWhiteSpace(tenant.OwnerEmail)`:
       * Look up `ownerUser = await userManager.FindByEmailAsync(tenant.OwnerEmail)`.
       * If `ownerUser is null`:
         * Decrypt `ownerPassword = protector.Unprotect(tenant.EncryptedOwnerPassword!)`.
         * Create `ApplicationUser { Id = Guid.NewGuid(), UserName = tenant.OwnerEmail, Email = tenant.OwnerEmail, EmailConfirmed = true }` via `userManager.CreateAsync(ownerUser, ownerPassword)` (throwing `InvalidOperationException` if creation fails).
       * If `!await userManager.IsInRoleAsync(ownerUser, TenantRoles.TenantOwner)`:
         * Call `await userManager.AddToRoleAsync(ownerUser, TenantRoles.TenantOwner)`.
7. **Finalize Active State & Wipe Sensitive Onboarding Secret**:
   * `tenant.EncryptedConnectionString = protector.Protect(rawTenantConnString)`
   * `tenant.Status = TenantStatus.Active`
   * `tenant.FailureReason = null`
   * `tenant.EncryptedOwnerPassword = null`
   * `tenant.ProvisionedAt = timeProvider.GetUtcNow()`
   * `tenant.UpdatedAt = tenant.ProvisionedAt.Value`
   * `await catalogDb.SaveChangesAsync(ct)`
   * `await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct)`
8. **Failure Compensation (`catch (Exception ex)`)**:
   * Log error with `tenantId` and `tenant.Slug`.
   * Reload/update `tenant`:
     * `tenant.Status = TenantStatus.Failed`
     * `tenant.FailureReason = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message`
     * `tenant.UpdatedAt = timeProvider.GetUtcNow()`
     * Keep `tenant.EncryptedOwnerPassword` intact so `retry-provisioning` can succeed later.
   * Save changes to `CatalogDbContext` and call `await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, CancellationToken.None)`.

### 4.3. Startup Recovery for Stuck `Provisioning` Tenants (`Program.cs`)
On application startup (when `MultiTenancy:Provisioning:RecoverStuckProvisioningOnStartup` is `true`):
* Query `CatalogDbContext.Tenants.Where(t => t.Status == TenantStatus.Provisioning).Select(t => t.Id).ToListAsync()`.
* For each stuck `tenantId`, publish `await bus.SendAsync(new StartTenantProvisioningCommand(tenantId))`.

---

## 5. Fleet Schema Migration Pipeline (>500 Tenant DBs)

### 5.1. Contracts (`Common.MultiTenancy`)
```csharp
namespace Common.MultiTenancy;

public record TenantMigrationItemResult(
    Guid TenantId,
    string Slug,
    bool Succeeded,
    int AppliedMigrationsCount,
    string? ErrorMessage,
    long DurationMs);

public record FleetMigrationSummaryResult(
    int TotalTenants,
    int SucceededCount,
    int FailedCount,
    long TotalDurationMs,
    IReadOnlyList<TenantMigrationItemResult> Results);

public interface ITenantFleetMigrator
{
    Task<TenantMigrationItemResult> MigrateTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<FleetMigrationSummaryResult> MigrateAllActiveTenantsAsync(CancellationToken ct = default);
}
```

### 5.2. `TenantFleetMigrator` Implementation (`ExamPrism.Infrastructure.MultiTenancy`)
* **`MigrateTenantAsync(Guid tenantId, CancellationToken ct)`**:
  1. Queries `CatalogDbContext.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct)`.
  2. If `tenant is null` or `string.IsNullOrWhiteSpace(tenant.EncryptedConnectionString)`, returns `TenantMigrationItemResult` with `Succeeded = false`.
  3. Starts `Stopwatch`. Decrypts `tenant.EncryptedConnectionString` via `IConnectionStringProtector` and normalizes via `ITenantConnectionStringNormalizer`.
  4. Constructs `AppDbContext` with `UseNpgsql(normalizedConnString)`.
  5. Fetches `pending = (await tenantDb.Database.GetPendingMigrationsAsync(ct)).Count()`.
  6. Executes `await tenantDb.Database.MigrateAsync(ct)`.
  7. Returns `TenantMigrationItemResult(tenant.Id, tenant.Slug, Succeeded: true, AppliedMigrationsCount: pending, ErrorMessage: null, sw.ElapsedMilliseconds)`.
  8. On `catch (Exception ex)`: logs error and returns `TenantMigrationItemResult(tenant.Id, tenant.Slug, Succeeded: false, AppliedMigrationsCount: 0, ErrorMessage: ex.Message, sw.ElapsedMilliseconds)`.
* **`MigrateAllActiveTenantsAsync(CancellationToken ct)`**:
  1. Queries all active tenant IDs (`Where(t => t.Status == TenantStatus.Active && t.EncryptedConnectionString != null)`).
  2. Uses `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = Math.Max(1, options.Value.FleetMigration.MaxDegreeOfParallelism)` (default `10`).
  3. Collects results into a thread-safe `ConcurrentBag<TenantMigrationItemResult>`, orders by `Slug`, and returns `FleetMigrationSummaryResult`.

### 5.3. Admin Fleet Migration Endpoints & Handlers (`ExamPrism.Features.Platform.Tenants` / `ExamPrism.Endpoints.Platform.Tenants`)
1. **`POST /api/platform/tenants/migrate`** (`MigrateAllTenantsEndpoint` / `MigrateAllTenantsHandler`):
   * Metadata: `.WithTags("Platform Tenants")`, `.RequirePlatform()`, `.RequireAuthorization(PlatformRoles.PolicyName)`.
   * Command: `MigrateAllTenantsCommand()` -> `Result<FleetMigrationSummaryResult>`.
   * Calls `await fleetMigrator.MigrateAllActiveTenantsAsync(ct)`.
2. **`POST /api/platform/tenants/{id:guid}/migrate`** (`MigrateSingleTenantEndpoint` / `MigrateSingleTenantHandler`):
   * Metadata: `.WithTags("Platform Tenants")`, `.RequirePlatform()`, `.RequireAuthorization(PlatformRoles.PolicyName)`.
   * Command: `MigrateSingleTenantCommand(Guid Id)` -> `Result<TenantMigrationItemResult>`.
   * Verifies tenant exists in `CatalogDbContext.Tenants` (`404 TENANT_NOT_FOUND` if missing) and has `Status == TenantStatus.Active` with non-empty `EncryptedConnectionString` (`409 INVALID_TENANT_STATUS_TRANSITION` otherwise).
   * Calls `await fleetMigrator.MigrateTenantAsync(command.Id, ct)`.

### 5.4. Startup Fleet Migration Hook (`Program.cs`)
When `MultiTenancy:FleetMigration:MigrateOnStartup` is `true`:
* Create an async scope on startup and execute `await fleetMigrator.MigrateAllActiveTenantsAsync()`.

---

## 6. Localization & Standardized Error Codes

Add resource entries to `SharedResources.resx` (`en-US`) and `SharedResources.vi.resx` (`vi-VN`):

| Error Code | HTTP Status | Resource Key | `en-US` (`SharedResources.resx`) | `vi-VN` (`SharedResources.vi.resx`) |
|---|---|---|---|---|
| `SLUG_ALREADY_TAKEN` | `409 Conflict` | `SlugAlreadyTaken` | `This organization subdomain is already taken or reserved.` | `Tên miền phụ của tổ chức này đã được sử dụng hoặc thuộc danh sách hệ thống.` |
| `INVALID_SLUG` | `400 Bad Request` | `InvalidSlug` | `Subdomain slug must be 1-63 lowercase alphanumeric characters or hyphens and cannot start or end with a hyphen.` | `Tên miền phụ chỉ được chứa chữ thường, chữ số hoặc dấu gạch ngang (1-63 ký tự) và không bắt đầu hay kết thúc bằng dấu gạch ngang.` |

---

## 7. Testing Strategy

### 7.1. Unit Tests
1. **`CheckTenantSlugHandlerTests`**:
   * Valid available slug returns `IsAvailable = true`.
   * Invalid DNS format returns `IsAvailable = false, ReasonCode = "INVALID_SLUG"`.
   * Reserved subdomain (`admin`, `www`, `api`) returns `IsAvailable = false, ReasonCode = "RESERVED_SLUG"`.
   * Existing tenant slug returns `IsAvailable = false, ReasonCode = "SLUG_ALREADY_TAKEN"`.
2. **`CreateTenantHandlerTests` & `RetryTenantProvisioningHandlerTests`**:
   * Creates `Provisioning` tenant with encrypted owner password, invalidates cache, and dispatches `StartTenantProvisioningCommand`.
   * Rejects duplicate/reserved slugs (`409 SLUG_ALREADY_TAKEN`) and weak owner passwords (`400`).
   * `RetryTenantProvisioningHandler` transitions `Failed -> Provisioning`, clears `FailureReason`, and re-dispatches `StartTenantProvisioningCommand`; rejects `Active` or `Suspended` tenants (`409 INVALID_TENANT_STATUS_TRANSITION`).
3. **`MigrateTenantHandlersTests`**:
   * Tests `MigrateAllTenantsHandler` and `MigrateSingleTenantHandler` (including `404` on unknown tenant and `409` on unprovisioned tenant).
4. **`ArchitectureTests`**:
   * Confirms all new handlers and endpoints in `ExamPrism.Features.Tenants`, `ExamPrism.Features.Platform.Tenants`, `ExamPrism.Endpoints.Tenants`, and `ExamPrism.Endpoints.Platform.Tenants` pass all architectural rules.

### 7.2. End-to-End Integration Tests (`TenantProvisioningAndFleetMigrationIntegrationTests`)
Executed against the real `Testcontainers.PostgreSql` instance in `CustomWebApplicationFactory`:
1. **Self-Service Onboarding & Background Provisioning E2E**:
   * `GET /api/tenants/check-slug/sunrise-academy` on `http://localhost` returns `isAvailable: true`; `GET /api/tenants/check-slug/admin` returns `isAvailable: false` (`RESERVED_SLUG`).
   * `POST /api/tenants` on `http://localhost` with `{ slug: "sunrise-academy", name: "Sunrise Academy", ownerEmail: "owner@sunrise.edu", ownerPassword: "OwnerPassword123!" }` returns `202 Accepted` (`status: "Provisioning"`).
   * Poll `GET /api/tenants/sunrise-academy/status` until `status == "Active"` (proving Wolverine background handler created physical database `examprism_tenant_sunrise_academy`, applied `AppDbContext` migrations, seeded `TenantOwner`, encrypted connection string, and wiped `EncryptedOwnerPassword` in `CatalogDbContext`).
   * Immediately call `POST http://sunrise-academy.localhost/api/auth/login` with `owner@sunrise.edu` / `OwnerPassword123!` -> returns `200 OK` and authenticates against the newly provisioned physical database.
2. **Dedicated PostgreSQL User Provisioning Mode**:
   * Test `TenantProvisioner` with `CreateDedicatedPostgresUser = true` -> creates dedicated PostgreSQL role `examprism_u_{slug}`, provisions DB owned by that role, migrates, and connects using the role's credentials.
3. **Admin Retry Provisioning Flow**:
   * Seed or trigger a `Failed` tenant with `OwnerEmail` and `EncryptedOwnerPassword` -> call `POST /api/platform/tenants/{id}/retry-provisioning` as `SuperAdmin` -> returns `202 Accepted` -> background worker completes provisioning to `Active`.
4. **Fleet Schema Migration API E2E**:
   * Call `POST /api/platform/tenants/migrate` as `SuperAdmin` on `http://localhost` -> returns `200 OK` with `SucceededCount >= 2, FailedCount == 0`.
   * Call `POST /api/platform/tenants/{tenantAId}/migrate` as `SuperAdmin` -> returns `200 OK` with `Succeeded == true`.
