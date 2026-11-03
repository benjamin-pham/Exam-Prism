# Design Specification: Platform Identity & Admin Tenant Management

**Date:** 2026-11-03  
**Status:** Draft (Pending User Spec Review)  
**Scope:** Architectural Subsystem — Sub-project 2 of 3  

---

## 1. Context, Goals, and Scope

ExamPrism has completed **Sub-project 1 — Multi-Tenant Core Foundation** (`docs/superpowers/specs/2026-11-02-multitenant-core-foundation-design.md`), establishing:
* Subdomain-based tenant resolution (`SubdomainParser`, `SubdomainMultiTenantStrategy`) and ciphertext-cached catalog lookup (`CatalogMultiTenantStore` backed by `HybridCache` and `ITenantCacheInvalidator`).
* Physical **Database-per-Tenant** isolation (`CatalogDbContext` for the master catalog database, dynamic fail-fast `AppDbContext` for >500 tenant databases with normalized Npgsql connection pooling).
* HTTP lifecycle guards (`TenantAccessGuardMiddleware`), endpoint tenancy boundary filters (`.RequireTenant()`, `.RequirePlatform()`), and cross-cutting tenant isolation across JWT (`tenant_id` claim), Host-Only cookies, `HybridCacheService` (`t:{id}:` vs `platform:`), `RateLimitingSetup`, and `TenantWolverineMiddleware`.

**Sub-project 2 (This Specification)** delivers **Platform Identity & Admin Tenant Management** on the central Master/Catalog database (`CatalogDbContext`):
1. **Dual ASP.NET Core Identity Architecture**:
   * Keep tenant users (`ApplicationUser`, `IdentityRole<Guid>`, `RefreshToken`) isolated inside each tenant's physical database (`AppDbContext`).
   * Add platform administration identity (`PlatformUser`, `PlatformRole`, `PlatformRefreshToken`) to the central master database (`CatalogDbContext`).
2. **Closed Platform Onboarding & Idempotent SuperAdmin Seeding**:
   * No public self-registration endpoint on the platform domain.
   * Configurable, idempotent `PlatformAdminSeeder` that ensures the `SuperAdmin` role and initial SuperAdmin account exist in `CatalogDbContext`.
3. **Cryptographic & Context Isolation for Platform vs. Tenant JWTs**:
   * Platform access tokens omit `tenant_id` and carry explicit `token_scope = "platform"` plus `ClaimTypes.Role` (`SuperAdmin`) claims.
   * `JwtTenantValidator` enforces two-way isolation: tenant tokens are rejected on platform hosts, and any token missing `token_scope == "platform"` (or carrying `tenant_id`) is rejected on platform hosts with `401 TENANT_TOKEN_MISMATCH`.
4. **Platform Auth & Admin Tenant Management Vertical Slices**:
   * Platform Auth APIs (`/api/platform/auth/login`, `/refresh`, `/logout`, `/me`) bound with `.RequirePlatform()`.
   * Admin Tenant Management APIs (`GET /api/platform/tenants`, `GET /api/platform/tenants/{id}`, `PATCH /api/platform/tenants/{id}`, `POST /api/platform/tenants/{id}/suspend`, `POST /api/platform/tenants/{id}/reactivate`) bound with `.RequirePlatform()` and `.RequireAuthorization("PlatformAdmin")`, featuring instant catalog and tenant-umbrella cache eviction.

### 1.1. Non-Goals of Sub-project 2
* Public self-service tenant provisioning (`POST /api/tenants`), background database creation worker (`CREATE DATABASE` + `MigrateAsync`), and fleet-wide rolling schema migration runner (deferred to **Sub-project 3**).

---

## 2. Persistence & Dual Identity Architecture

### 2.1. Platform Identity Entities (`ExamPrism.Infrastructure.Identity`)
* **`PlatformUser : IdentityUser<Guid>`**:
  ```csharp
  namespace ExamPrism.Infrastructure.Identity;

  using Microsoft.AspNetCore.Identity;

  public class PlatformUser : IdentityUser<Guid>
  {
      public ICollection<PlatformRefreshToken> RefreshTokens { get; set; } = new List<PlatformRefreshToken>();
  }
  ```
* **`PlatformRole : IdentityRole<Guid>`**:
  ```csharp
  namespace ExamPrism.Infrastructure.Identity;

  using Microsoft.AspNetCore.Identity;

  public class PlatformRole : IdentityRole<Guid>
  {
      public PlatformRole() { }
      public PlatformRole(string roleName) : base(roleName) { }
  }
  ```
* **`PlatformRefreshToken`**:
  ```csharp
  namespace ExamPrism.Infrastructure.Identity;

  public class PlatformRefreshToken
  {
      public Guid Id { get; set; }
      public Guid UserId { get; set; }
      public PlatformUser User { get; set; } = null!;
      public string TokenHash { get; set; } = string.Empty;
      public DateTimeOffset ExpiresAt { get; set; }
      public DateTimeOffset CreatedAt { get; set; }
      public DateTimeOffset? RevokedAt { get; set; }
      public string? ReplacedByTokenHash { get; set; }

      public bool IsExpired(TimeProvider timeProvider) => timeProvider.GetUtcNow() >= ExpiresAt;
      public bool IsRevoked => RevokedAt != null;
      public bool IsActive(TimeProvider timeProvider) => !IsRevoked && !IsExpired(timeProvider);
  }
  ```

### 2.2. `CatalogDbContext` Upgrade & Catalog Migration
* `CatalogDbContext` (`ExamPrism.Infrastructure.Persistence.Catalog`) changes its base class from `DbContext` to `IdentityDbContext<PlatformUser, PlatformRole, Guid>`:
  * Retains `public DbSet<Tenant> Tenants => Set<Tenant>();` with its existing table/index configuration (`Tenants`, `IX_Tenants_Slug`, `IX_Tenants_Status`).
  * Adds `public DbSet<PlatformRefreshToken> PlatformRefreshTokens => Set<PlatformRefreshToken>();`:
    * Table name: `"PlatformRefreshTokens"`
    * Primary key: `Id`
    * `TokenHash`: required, `HasMaxLength(128)`, unique index `IX_PlatformRefreshTokens_TokenHash`
    * `UserId`: indexed (`IX_PlatformRefreshTokens_UserId`), foreign key to `PlatformUser.RefreshTokens` with `DeleteBehavior.Cascade`.
* **Catalog EF Core Migration**:
  * Generate migration `AddPlatformIdentity` under `Infrastructure/Persistence/Migrations/Catalog` using `--context CatalogDbContext`.
  * `Infrastructure/Persistence/Migrations/Tenant` (`AppDbContext`) remains completely untouched.

### 2.3. Dual `AddIdentityCore` Registration (`DependencyInjection.cs`)
Because `PlatformUser` and `PlatformRole` are distinct closed generic types from `ApplicationUser` and `IdentityRole<Guid>`, registering both in `IServiceCollection` does not cause service descriptor collisions:
```csharp
// Tenant Identity (AppDbContext — per-tenant physical DB)
services.AddIdentityCore<ApplicationUser>(ConfigurePasswordAndUserOptions)
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// Platform Identity (CatalogDbContext — master catalog DB)
services.AddIdentityCore<PlatformUser>(ConfigurePasswordAndUserOptions)
    .AddRoles<PlatformRole>()
    .AddEntityFrameworkStores<CatalogDbContext>()
    .AddDefaultTokenProviders();
```
* `UserManager<ApplicationUser>` and `RoleManager<IdentityRole<Guid>>` resolve `AppDbContext`.
* `UserManager<PlatformUser>` and `RoleManager<PlatformRole>` resolve `CatalogDbContext` directly and work in Platform Context (`ITenantContext.HasTenant == false`) without ever touching `AppDbContext`.

---

## 3. SuperAdmin Configuration & Startup Seeding

### 3.1. Configuration Schema (`appsettings.json` & `appsettings.Development.json`)
Add `"PlatformAdmin"` section:
* In `appsettings.json`:
  ```json
  "PlatformAdmin": {
    "SeedOnStartup": false,
    "Email": "",
    "Password": ""
  }
  ```
* In `appsettings.Development.json`:
  ```json
  "PlatformAdmin": {
    "SeedOnStartup": true,
    "Email": "admin@examprism.com",
    "Password": "AdminPassword123!"
  }
  ```

### 3.2. `PlatformAdminOptions`, `PlatformRoles` & `PlatformAdminSeeder`
* **`Common.Security.PlatformRoles`**:
  ```csharp
  namespace Common.Security;

  public static class PlatformRoles
  {
      public const string SuperAdmin = "SuperAdmin";
      public const string PolicyName = "PlatformAdmin";
  }
  ```
* **`ExamPrism.Infrastructure.Identity.PlatformAdminOptions`**:
  * `public const string SectionName = "PlatformAdmin";`
  * `bool SeedOnStartup { get; set; }`
  * `string Email { get; set; } = string.Empty;`
  * `string Password { get; set; } = string.Empty;`
* **`ExamPrism.Infrastructure.Identity.PlatformAdminSeeder`**:
  * Registered as `Scoped` in `DependencyInjection.cs`.
  * `public async Task SeedAsync(CancellationToken ct = default)`:
    1. Ensures `PlatformRole` with name `PlatformRoles.SuperAdmin` exists via `RoleManager<PlatformRole>`; creates it if missing (throwing `InvalidOperationException` with joined `IdentityError` descriptions if creation fails).
    2. If `string.IsNullOrWhiteSpace(options.Email)` or `string.IsNullOrWhiteSpace(options.Password)`: logs warning and returns after ensuring the role exists.
    3. Looks up `PlatformUser` by `userManager.FindByEmailAsync(options.Email)`.
    4. If `null`, creates `new PlatformUser { Id = Guid.NewGuid(), UserName = options.Email, Email = options.Email, EmailConfirmed = true }` with `userManager.CreateAsync(user, options.Password)` (throwing `InvalidOperationException` if password validation or creation fails).
    5. Checks `await userManager.IsInRoleAsync(user, PlatformRoles.SuperAdmin)`; if `false`, calls `await userManager.AddToRoleAsync(user, PlatformRoles.SuperAdmin)`.
  * In `Program.cs`, before `app.Run()`, if `platformAdminOptions.SeedOnStartup` is `true`, create an async scope and execute `await seeder.SeedAsync()`.

---

## 4. JWT Isolation & Authorization Policy

### 4.1. Claim Constants (`Common.Security.CustomClaimTypes`)
```csharp
namespace Common.Security;

public static class CustomClaimTypes
{
    public const string TenantId = "tenant_id";
    public const string TokenScope = "token_scope";
    public const string PlatformScopeValue = "platform";
}
```

### 4.2. `ITokenService` & `TokenService`
Extend `ITokenService` (`ExamPrism.Infrastructure.Identity`):
```csharp
public interface ITokenService
{
    string GenerateAccessToken(ApplicationUser user);
    string GeneratePlatformAccessToken(PlatformUser user, IEnumerable<string> roles);
    (string RawToken, string TokenHash) GenerateRefreshToken();
    string HashToken(string rawToken);
}
```
* **`GeneratePlatformAccessToken(PlatformUser user, IEnumerable<string> roles)`**:
  * Guards `if (tenantContext.HasTenant) throw new InvalidOperationException("Platform access tokens cannot be generated inside an active tenant context.");`
  * Emits claims:
    * `JwtRegisteredClaimNames.Sub` = `user.Id.ToString()`
    * `ClaimTypes.NameIdentifier` = `user.Id.ToString()`
    * `JwtRegisteredClaimNames.Email` = `user.Email ?? string.Empty`
    * `ClaimTypes.Email` = `user.Email ?? string.Empty`
    * `JwtRegisteredClaimNames.Jti` = `Guid.NewGuid().ToString()`
    * `CustomClaimTypes.TokenScope` = `CustomClaimTypes.PlatformScopeValue` (`"platform"`)
    * For each distinct non-empty role in `roles`: `new Claim(ClaimTypes.Role, role)`
  * Does **not** emit `CustomClaimTypes.TenantId`.

### 4.3. Two-Way `JwtTenantValidator` Enforcement
Update `JwtTenantValidator.ValidateAsync(TokenValidatedContext context)`:
1. **Active Tenant Context (`tenantInfo is { Status: TenantStatus.Active }`)**:
   * Extract `claimTenantId = context.Principal?.FindFirst(CustomClaimTypes.TenantId)?.Value`.
   * Extract `tokenScope = context.Principal?.FindFirst(CustomClaimTypes.TokenScope)?.Value`.
   * If `string.IsNullOrWhiteSpace(claimTenantId)` OR `!string.Equals(claimTenantId, tenantInfo.Id, StringComparison.OrdinalIgnoreCase)` OR `string.Equals(tokenScope, CustomClaimTypes.PlatformScopeValue, StringComparison.OrdinalIgnoreCase)`:
     * Set `context.HttpContext.Items[AuthFailureItemKey] = TenantTokenMismatchErrorCode` (`"TENANT_TOKEN_MISMATCH"`) and call `context.Fail("Token tenant mismatch.")`.
2. **Platform Context (`tenantInfo is null`)**:
   * Extract `claimTenantId = context.Principal?.FindFirst(CustomClaimTypes.TenantId)?.Value`.
   * Extract `tokenScope = context.Principal?.FindFirst(CustomClaimTypes.TokenScope)?.Value`.
   * If `!string.IsNullOrWhiteSpace(claimTenantId)` OR `!string.Equals(tokenScope, CustomClaimTypes.PlatformScopeValue, StringComparison.OrdinalIgnoreCase)`:
     * Set `context.HttpContext.Items[AuthFailureItemKey] = TenantTokenMismatchErrorCode` (`"TENANT_TOKEN_MISMATCH"`) and call `context.Fail("Platform context requires a valid platform token.")`.

### 4.4. Authorization Policy (`DependencyInjection.cs`)
Register the `"PlatformAdmin"` policy:
```csharp
services.AddAuthorizationBuilder()
    .AddPolicy(PlatformRoles.PolicyName, policy =>
        policy.RequireAuthenticatedUser()
              .RequireClaim(CustomClaimTypes.TokenScope, CustomClaimTypes.PlatformScopeValue)
              .RequireRole(PlatformRoles.SuperAdmin));
```

---

## 5. Platform Auth Vertical Slices & Endpoints

All Platform Auth handlers live in `namespace ExamPrism.Features.Platform.Auth` and all Platform Auth endpoints live in `namespace ExamPrism.Endpoints.Platform.Auth`, adhering strictly to `ArchitectureTests`.

### 5.1. `POST /api/platform/auth/login`
* **Endpoint**: `PlatformLoginEndpoint` (`.WithTags("Platform Authentication")`, `.RequirePlatform()`, `.RequireRateLimiting(RateLimitPolicies.Auth)`).
* **Contract**:
  * `PlatformLoginCommand([Required, EmailAddress] string Email, [Required] string Password)` + `PlatformLoginCommandValidator` (`REQUIRED`, `INVALID_EMAIL`).
  * `PlatformLoginResult(Guid Id, string Email, IReadOnlyList<string> Roles, [property: JsonIgnore] string AccessToken = "", [property: JsonIgnore] string RefreshToken = "")`.
* **Handler (`PlatformLoginHandler`)**:
  * Injects `UserManager<PlatformUser>`, `CatalogDbContext`, `ITokenService`, `IOptions<JwtOptions>`, `TimeProvider`.
  * Validates credentials via `FindByEmailAsync` + `CheckPasswordAsync`; returns `Error.Unauthorized("INVALID_CREDENTIALS", "Invalid email or password.")` on failure.
  * Fetches `roles = await userManager.GetRolesAsync(user)`.
  * Generates platform access token (`tokenService.GeneratePlatformAccessToken(user, roles)`) and refresh token (`tokenService.GenerateRefreshToken()`), stores `PlatformRefreshToken` in `CatalogDbContext.PlatformRefreshTokens`, saves changes, and returns `PlatformLoginResult`.
  * `PlatformLoginEndpoint` sets Host-Only `access_token` and `refresh_token` cookies via `AuthCookieHelper.SetAuthCookies(...)`.

### 5.2. `POST /api/platform/auth/refresh`
* **Endpoint**: `PlatformRefreshEndpoint` (`.WithTags("Platform Authentication")`, `.RequirePlatform()`, `.RequireRateLimiting(RateLimitPolicies.Auth)`).
  * Reads `refresh_token` cookie from `httpContext.Request.Cookies`, invokes `PlatformRefreshTokenCommand(rawRefreshToken)`.
  * On failure, clears cookies (`AuthCookieHelper.ClearAuthCookies`) and returns `result.ToProblemResult(httpContext)`.
  * On success, sets rotated cookies (`AuthCookieHelper.SetAuthCookies`) and returns `200 OK`.
* **Handler (`PlatformRefreshTokenHandler`)**:
  * Mirrors tenant `RefreshTokenHandler` against `CatalogDbContext.PlatformRefreshTokens` + `UserManager<PlatformUser>`:
    * Missing/unknown hash -> `Error.Unauthorized("INVALID_REFRESH_TOKEN", "Invalid refresh token.")`.
    * Reuse detection (`existingToken.IsRevoked`) -> revokes all active `PlatformRefreshTokens` for `existingToken.UserId`, saves changes, returns `Error.Unauthorized("INVALID_REFRESH_TOKEN", "Refresh token has been revoked.")`.
    * Expired (`existingToken.IsExpired(timeProvider)`) -> returns `Error.Unauthorized("REFRESH_TOKEN_EXPIRED", "Refresh token has expired.")`.
    * Rotates token (`RevokedAt = now`, `ReplacedByTokenHash = newHash`), adds new `PlatformRefreshToken`, fetches current `userManager.GetRolesAsync(user)`, generates new platform access token, saves changes, returns `PlatformLoginResult`.

### 5.3. `POST /api/platform/auth/logout`
* **Endpoint**: `PlatformLogoutEndpoint` (`.WithTags("Platform Authentication")`, `.RequirePlatform()`, `.RequireRateLimiting(RateLimitPolicies.Auth)`).
  * Reads `refresh_token` cookie, invokes `PlatformLogoutCommand(rawRefreshToken)`, calls `AuthCookieHelper.ClearAuthCookies`, returns `200 OK` (`PlatformLogoutResult(true)`).
* **Handler (`PlatformLogoutHandler`)**:
  * Looks up active `PlatformRefreshToken` by hash in `CatalogDbContext` and sets `RevokedAt = timeProvider.GetUtcNow()` if found and active.

### 5.4. `GET /api/platform/auth/me`
* **Endpoint**: `GetPlatformMeEndpoint` (`.WithTags("Platform Authentication")`, `.RequirePlatform()`, `.RequireAuthorization(PlatformRoles.PolicyName)`).
* **Contract & Handler (`GetPlatformMeQuery` / `GetPlatformMeHandler`)**:
  * Injects `IUserContext` and `UserManager<PlatformUser>`.
  * Resolves `PlatformUser` by `userContext.UserId`, fetches `roles = await userManager.GetRolesAsync(user)`, returns `GetPlatformMeResult(Guid Id, string Email, IReadOnlyList<string> Roles)` (or `Error.Unauthorized("UNAUTHORIZED", ...)` / `Error.NotFound("USER_NOT_FOUND", ...)`).

---

## 6. Admin Tenant Management Vertical Slices & Cache Eviction

All Admin Tenant Management handlers live in `namespace ExamPrism.Features.Platform.Tenants` and endpoints live in `namespace ExamPrism.Endpoints.Platform.Tenants`. Every endpoint enforces `.RequirePlatform()`, `.RequireAuthorization(PlatformRoles.PolicyName)`, and `.WithTags("Platform Tenants")`.

### 6.1. Extending `CatalogMultiTenantStore.InvalidateTenantAsync`
Update `CatalogMultiTenantStore.InvalidateTenantAsync(Guid tenantId, string slug, CancellationToken ct = default)`:
* In addition to removing keys `"catalog:tenant:slug:{slug}"`, `"catalog:tenant:id:{id:D}"` and tags `"catalog:tenant:slug:{slug}"`, `"catalog:tenant:{id:D}"`, also add tag `"t:{tenantId:D}"` (when `tenantId != Guid.Empty`).
* Calling `InvalidateTenantAsync` therefore evicts both the catalog lookup cache and all tenant-scoped business cache entries stored via `HybridCacheService`.

### 6.2. Endpoints & Handlers

#### 1. `GET /api/platform/tenants` (`ListTenantsEndpoint` / `ListTenantsHandler`)
* **Query** (bound via `[AsParameters] ListTenantsQuery query` in `ListTenantsEndpoint`):
  ```csharp
  public record ListTenantsQuery(
      int Page = 1,
      int PageSize = 20,
      TenantStatus? Status = null,
      string? Search = null);
  ```
* **Validator (`ListTenantsQueryValidator : AbstractValidator<ListTenantsQuery>`)**:
  * `Page >= 1` (`WithErrorCode("INVALID_PAGE")`)
  * `PageSize` between `1` and `100` (`WithErrorCode("INVALID_PAGE_SIZE")`)
* **Response**:
  ```csharp
  public record TenantSummaryItem(
      Guid Id,
      string Slug,
      string Name,
      TenantStatus Status,
      DateTimeOffset CreatedAt,
      DateTimeOffset? ProvisionedAt,
      DateTimeOffset UpdatedAt);

  public record ListTenantsResult(
      IReadOnlyList<TenantSummaryItem> Items,
      int TotalCount,
      int Page,
      int PageSize);
  ```
* **Behavior**:
  * Queries `CatalogDbContext.Tenants.AsNoTracking()`.
  * Filters by `Status` if provided.
  * Filters by `Search` (trimmed, case-insensitive substring match against `Slug` or `Name`) if non-whitespace.
  * Orders by `CreatedAt` descending, then `Slug` ascending; projects to `TenantSummaryItem` (never exposing `EncryptedConnectionString`).

#### 2. `GET /api/platform/tenants/{id:guid}` (`GetTenantByIdEndpoint` / `GetTenantByIdHandler`)
* **Query**: `GetTenantByIdQuery(Guid Id)`
* **Response**:
  ```csharp
  public record TenantDetailResult(
      Guid Id,
      string Slug,
      string Name,
      TenantStatus Status,
      string? FailureReason,
      DateTimeOffset CreatedAt,
      DateTimeOffset? ProvisionedAt,
      DateTimeOffset UpdatedAt);
  ```
* **Behavior**:
  * Queries `CatalogDbContext.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == query.Id, ct)`.
  * If `null`, returns `Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.")`.

#### 3. `PATCH /api/platform/tenants/{id:guid}` (`UpdateTenantEndpoint` / `UpdateTenantHandler`)
* **Request Body & Command**:
  * Endpoint request DTO: `UpdateTenantRequest(string Name)` bound from JSON body, validated by `UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>` (`Name` `NotEmpty().WithErrorCode("REQUIRED").MaximumLength(200).WithErrorCode("MAX_LENGTH")`), then mapped with route `Guid id` into `UpdateTenantCommand(Guid Id, string Name)` (which also has `UpdateTenantCommandValidator`).
* **Behavior**:
  * Loads `Tenant` from `CatalogDbContext.Tenants`. If `null`, returns `Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.")`.
  * Updates `tenant.Name = command.Name.Trim()`, `tenant.UpdatedAt = timeProvider.GetUtcNow()`.
  * Calls `await db.SaveChangesAsync(ct)` and `await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct)`.
  * Returns `Result<TenantDetailResult>`.

#### 4. `POST /api/platform/tenants/{id:guid}/suspend` (`SuspendTenantEndpoint` / `SuspendTenantHandler`)
* **Command**: `SuspendTenantCommand(Guid Id)`
* **Behavior**:
  * Loads `Tenant` from `CatalogDbContext.Tenants`. If `null`, returns `Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.")`.
  * State guard: Only an `Active` tenant can be suspended. If `tenant.Status != TenantStatus.Active`, returns `Error.Conflict("INVALID_TENANT_STATUS_TRANSITION", "Only an active organization workspace can be suspended.")`.
  * Sets `tenant.Status = TenantStatus.Suspended`, `tenant.UpdatedAt = timeProvider.GetUtcNow()`.
  * Calls `await db.SaveChangesAsync(ct)` and `await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct)`.
  * Returns `Result<TenantDetailResult>`.

#### 5. `POST /api/platform/tenants/{id:guid}/reactivate` (`ReactivateTenantEndpoint` / `ReactivateTenantHandler`)
* **Command**: `ReactivateTenantCommand(Guid Id)`
* **Behavior**:
  * Loads `Tenant` from `CatalogDbContext.Tenants`. If `null`, returns `Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.")`.
  * State guard: Only a `Suspended` tenant with a non-whitespace `EncryptedConnectionString` can be reactivated. If `tenant.Status != TenantStatus.Suspended || string.IsNullOrWhiteSpace(tenant.EncryptedConnectionString)`, returns `Error.Conflict("INVALID_TENANT_STATUS_TRANSITION", "Only a suspended organization workspace with a valid database connection can be reactivated.")`.
  * Sets `tenant.Status = TenantStatus.Active`, `tenant.UpdatedAt = timeProvider.GetUtcNow()`.
  * Calls `await db.SaveChangesAsync(ct)` and `await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct)`.
  * Returns `Result<TenantDetailResult>`.

---

## 7. Localization & Standardized Error Codes

Add resource entries to `SharedResources.resx` (`en-US`) and `SharedResources.vi.resx` (`vi-VN`):

| Error Code | HTTP Status | Resource Key | `en-US` (`SharedResources.resx`) | `vi-VN` (`SharedResources.vi.resx`) |
|---|---|---|---|---|
| `INVALID_TENANT_STATUS_TRANSITION` | `409 Conflict` | `InvalidTenantStatusTransition` | `The organization workspace cannot transition to the requested status from its current state.` | `Không thể chuyển đổi trạng thái của tổ chức từ trạng thái hiện tại.` |
| `TENANT_NOT_FOUND` | `404 Not Found` | `TenantNotFound` (existing) | `The requested organization was not found.` | `Không tìm thấy tổ chức theo tên miền này.` |
| `INVALID_PAGE` | `400 Bad Request` | `InvalidPage` | `Page number must be greater than or equal to 1.` | `Số trang phải lớn hơn hoặc bằng 1.` |
| `INVALID_PAGE_SIZE` | `400 Bad Request` | `InvalidPageSize` | `Page size must be between 1 and 100.` | `Kích thước trang phải nằm trong khoảng từ 1 đến 100.` |

---

## 8. Testing Strategy

### 8.1. Unit & Schema Tests
1. **`CatalogDbContextSchemaTests`**:
   * Verify `CatalogDbContext` model includes `PlatformUser`, `PlatformRole`, and `PlatformRefreshTokens` (`IX_PlatformRefreshTokens_TokenHash` unique index, `IX_PlatformRefreshTokens_UserId` index) alongside `Tenants`.
   * Verify `Catalog` and `Tenant` migration assemblies remain strictly disjoint (`AddPlatformIdentity` only in Catalog).
2. **`PlatformAdminSeederTests`**:
   * Idempotent creation of `SuperAdmin` role and initial `PlatformUser`.
   * Running `SeedAsync` twice does not duplicate users or fail.
   * Blank `Email`/`Password` still creates the `SuperAdmin` role without creating a user.
3. **`TokenServiceTests` & `CrossCuttingTenantIsolationUnitTests`**:
   * `GeneratePlatformAccessToken` emits `token_scope = "platform"`, `ClaimTypes.Role = "SuperAdmin"`, omits `tenant_id`, and throws `InvalidOperationException` if called when `ITenantContext.HasTenant == true`.
   * `JwtTenantValidator` allows platform tokens (`token_scope == "platform"`, no `tenant_id`) on Platform hosts, rejects tokens missing `token_scope == "platform"` on Platform hosts (`TENANT_TOKEN_MISMATCH`), and rejects platform tokens on Tenant hosts (`TENANT_TOKEN_MISMATCH`).
4. **`PlatformTenantHandlersTests`**:
   * Unit tests for `ListTenantsHandler` (pagination, status filter, search filter), `GetTenantByIdHandler`, `UpdateTenantHandler`, `SuspendTenantHandler` (`Active -> Suspended` + cache invalidation; `Provisioning`/`Suspended` -> `409 INVALID_TENANT_STATUS_TRANSITION`), and `ReactivateTenantHandler` (`Suspended -> Active` + cache invalidation; `Active`/`Provisioning` -> `409`).
5. **`ArchitectureTests`**:
   * All new `ExamPrism.Features.Platform.*` handlers and `ExamPrism.Endpoints.Platform.*` endpoints pass every architectural rule without modification to the rules.

### 8.2. End-to-End Integration Tests (`PlatformAdminIntegrationTests`)
Extend `CustomWebApplicationFactory` to seed the test `SuperAdmin` (`admin@examprism.com` / `AdminPassword123!`) into `examprism_catalog_test` on `InitializeAsync()`:
1. **Platform Auth Flow (`/api/platform/auth/*`)**:
   * Login on `http://localhost/api/platform/auth/login` returns `200 OK` + sets Host-Only cookies -> `GET /api/platform/auth/me` returns `admin@examprism.com` with role `["SuperAdmin"]` -> `/refresh` rotates token -> `/logout` revokes token.
2. **Cross-Boundary & Cross-Token Rejection**:
   * Calling `/api/platform/auth/login` or `/api/platform/tenants` on `http://tenant-a.localhost` returns `404 PLATFORM_ONLY`.
   * Using a `tenant-a` user JWT against `http://localhost/api/platform/tenants` returns `401 TENANT_TOKEN_MISMATCH`.
   * Using a `SuperAdmin` platform JWT against `http://tenant-a.localhost/api/auth/me` returns `401 TENANT_TOKEN_MISMATCH`.
3. **Live Tenant Lifecycle & Instant Cache Invalidation**:
   * Register & login a user on `http://tenant-a.localhost` and verify `GET /api/auth/me` returns `200 OK` (populating `CatalogMultiTenantStore` cache for `tenant-a`).
   * From `http://localhost` as `SuperAdmin`, call `GET /api/platform/tenants?search=tenant-a` to find `tenantA.Id`.
   * Call `POST /api/platform/tenants/{tenantA.Id}/suspend` -> returns `200 OK` (`Status = Suspended`).
   * Immediately call `http://tenant-a.localhost/api/auth/me` with the tenant user's valid JWT -> returns `403 Forbidden` with `errorCode = "TENANT_SUSPENDED"` (proving instant cache eviction).
   * Call `POST /api/platform/tenants/{tenantA.Id}/reactivate` -> returns `200 OK` (`Status = Active`).
   * Immediately call `http://tenant-a.localhost/api/auth/me` -> returns `200 OK` again.
