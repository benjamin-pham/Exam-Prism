# Platform Identity & Admin Tenant Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver Sub-project 2 of ExamPrism's multi-tenant SaaS architecture — dual ASP.NET Core Identity on `CatalogDbContext` (`PlatformUser`, `PlatformRole`, `PlatformRefreshToken`), idempotent `SuperAdmin` startup seeding, two-way JWT scope isolation (`token_scope = "platform"` vs `tenant_id`), Platform Auth vertical slices (`/api/platform/auth/*`), and Admin Tenant Management APIs (`/api/platform/tenants/*`) with instant catalog and tenant-umbrella cache eviction.

**Architecture:** `CatalogDbContext` upgrades to `IdentityDbContext<PlatformUser, PlatformRole, Guid>` with `DbSet<PlatformRefreshToken>` and a dedicated `AddPlatformIdentity` Catalog migration while `AppDbContext` (`ApplicationUser`, `IdentityRole<Guid>`) remains isolated in each tenant's physical database. `TokenService` and `JwtTenantValidator` enforce strict cryptographic separation between platform tokens (`token_scope = "platform"`, no `tenant_id`) and tenant tokens (`tenant_id`, no `token_scope = "platform"`). Wolverine vertical slices under `ExamPrism.Features.Platform.{Auth,Tenants}` and Minimal API endpoints under `ExamPrism.Endpoints.Platform.{Auth,Tenants}` expose `.RequirePlatform()` + `"PlatformAdmin"` policy routes and evict both catalog and tenant `HybridCache` tags via `ITenantCacheInvalidator`.

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core 10 Minimal APIs (`IEndpoint`), ASP.NET Core Identity (`IdentityDbContext`), EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `Finbuckle.MultiTenant` 10.1.5, `Microsoft.Extensions.Caching.Hybrid` 10.0.0, WolverineFx 4.8.0, FluentValidation, xUnit, FluentAssertions, NSubstitute, `Testcontainers.PostgreSql` 4.16.0.

**Spec:** `docs/superpowers/specs/2026-11-03-platform-identity-admin-tenant-management-design.md`

## Global Constraints

- Target `.NET 10` (`net10.0`) with zero new NuGet packages required.
- Preserve all `ArchitectureTests` invariants:
  - Every handler in `ExamPrism.Features.*` must be `public sealed`, end with `Handler`, implement `IHandler<TRequest>` or `IHandler<TRequest, TResponse>`, declare a public constructor with **no default parameter values**, declare a single public instance `Handle` method whose first parameter is a `record` ending with `Command`, `Query`, or `Event`, and return `Task<Result>` or `Task<Result<TResult>>` where `TResult` ends with `Result`.
  - `ExamPrism.Features.*` must have zero references to `Microsoft.AspNetCore.{Http,Routing,Mvc}`.
  - Every endpoint in `ExamPrism.Endpoints.*` must be `public sealed`, end with `Endpoint` or `Endpoints`, implement `IEndpoint`, execute inline inside `MapEndpoint` without `Handle`/`HandleAsync` methods, and never reference `DbContext` (`AppDbContext` or `CatalogDbContext`).
- Keep Catalog and Tenant EF Core migrations strictly separate: `AddPlatformIdentity` must be generated under `Infrastructure/Persistence/Migrations/Catalog` (`--context CatalogDbContext`) without touching `Infrastructure/Persistence/Migrations/Tenant`.
- Never expose `Tenant.EncryptedConnectionString` in any response DTO (`TenantSummaryItem`, `TenantDetailResult`).
- All Platform Auth and Admin Tenant Management endpoints must enforce `.RequirePlatform()` (returning `404 PLATFORM_ONLY` on tenant subdomains), and Admin Tenant Management + `/api/platform/auth/me` endpoints must enforce `.RequireAuthorization(PlatformRoles.PolicyName)`.

## Review Focus

1. **Dual `AddIdentityCore` DI registration collision between `ApplicationUser` (`AppDbContext`) and `PlatformUser` (`CatalogDbContext`)**: `AddIdentityCore<PlatformUser>()` must use the distinct `PlatformRole` type (`AddRoles<PlatformRole>().AddEntityFrameworkStores<CatalogDbContext>()`) so `UserManager<PlatformUser>` and `RoleManager<PlatformRole>` resolve `CatalogDbContext` in Platform Context without resolving fail-fast `AppDbContext` or overwriting `UserManager<ApplicationUser>` (pinned in Task 1).
2. **Forged or unscoped JWT without `tenant_id` accepted on Platform hosts, or Platform JWT accepted on Tenant subdomains**: `JwtTenantValidator` must reject any token on a Platform host that lacks `token_scope == "platform"` (or carries `tenant_id`), and reject any token on a Tenant host that carries `token_scope == "platform"`, returning `401` `TENANT_TOKEN_MISMATCH` in both directions (pinned in Task 2).
3. **`PlatformAdminSeeder` running in `Program.cs` before `CatalogDbContext.Database.MigrateAsync()` in `CustomWebApplicationFactory` tests**: `CustomWebApplicationFactory.ConfigureWebHost` must set `PlatformAdmin:SeedOnStartup = "false"`, configure test `PlatformAdmin:Email` / `PlatformAdmin:Password`, and invoke `PlatformAdminSeeder.SeedAsync()` inside `MultiTenantTestDatabaseHelper.InitializeDatabasesAndSeedAsync` immediately after `catalogDb.Database.MigrateAsync()` (pinned in Task 2 & Task 4).
4. **Reactivating a `Provisioning` or `Failed` tenant that has no `EncryptedConnectionString`, or suspending an already non-`Active` tenant**: `SuspendTenantHandler` must allow only `TenantStatus.Active -> TenantStatus.Suspended` (`409 INVALID_TENANT_STATUS_TRANSITION` otherwise), and `ReactivateTenantHandler` must allow only `TenantStatus.Suspended -> TenantStatus.Active` when `EncryptedConnectionString` is non-whitespace (`409 INVALID_TENANT_STATUS_TRANSITION` otherwise) (pinned in Task 4).
5. **Stale cached `ExamPrismTenantInfo` (`Status == Active`) or cached tenant business data surviving in `HybridCache` after `SuspendTenantHandler` executes**: `CatalogMultiTenantStore.InvalidateTenantAsync` must evict `"catalog:tenant:slug:{slug}"`, `"catalog:tenant:id:{id:D}"`, and tag `"t:{id:D}"` so the very next HTTP request to `{slug}.localhost` with a still-unexpired tenant JWT immediately receives `403 TENANT_SUSPENDED` (pinned in Task 4).

---

### Task 1: Platform Identity Entities, `CatalogDbContext` Upgrade, Dual `AddIdentityCore` & Catalog Migration

**Files:**
- Create: `apps/backend/src/Common/Security/PlatformRoles.cs`
- Create: `apps/backend/src/Infrastructure/Identity/PlatformUser.cs`
- Create: `apps/backend/src/Infrastructure/Identity/PlatformRole.cs`
- Create: `apps/backend/src/Infrastructure/Identity/PlatformRefreshToken.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/Catalog/CatalogDbContext.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/Migrations/Catalog/*_AddPlatformIdentity.cs` (via `dotnet ef`)
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs:135-148`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CatalogDbContextSchemaTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`

**Interfaces:**
- Consumes: `ExamPrism.Infrastructure.Persistence.Catalog.Tenant`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore.IdentityDbContext<TUser, TRole, TKey>`
- Produces:
  - `Common.Security.PlatformRoles` (`public const string SuperAdmin = "SuperAdmin";`, `public const string PolicyName = "PlatformAdmin";`)
  - `ExamPrism.Infrastructure.Identity.PlatformUser : IdentityUser<Guid>` (`ICollection<PlatformRefreshToken> RefreshTokens`)
  - `ExamPrism.Infrastructure.Identity.PlatformRole : IdentityRole<Guid>`
  - `ExamPrism.Infrastructure.Identity.PlatformRefreshToken` (`Id`, `UserId`, `User`, `TokenHash`, `ExpiresAt`, `CreatedAt`, `RevokedAt`, `ReplacedByTokenHash`, `IsExpired(TimeProvider)`, `IsRevoked`, `IsActive(TimeProvider)`)
  - `ExamPrism.Infrastructure.Persistence.Catalog.CatalogDbContext : IdentityDbContext<PlatformUser, PlatformRole, Guid>` (`DbSet<Tenant> Tenants`, `DbSet<PlatformRefreshToken> PlatformRefreshTokens`)
  - Dual `AddIdentityCore<ApplicationUser>` + `AddIdentityCore<PlatformUser>` in `DependencyInjection.AddInfrastructure`

- [ ] **Step 1: Write failing schema & DI tests in `CatalogDbContextSchemaTests.cs` and `DependencyInjectionTests.cs`**

In `apps/backend/tests/ExamPrism.Tests/Infrastructure/CatalogDbContextSchemaTests.cs`, add:
- `CatalogDbContext_ModelMetadata_ConfiguresPlatformIdentityAndPlatformRefreshTokens`: builds `CatalogDbContext`, asserts `context.Model.FindEntityType(typeof(PlatformUser))` and `FindEntityType(typeof(PlatformRole))` exist, and `FindEntityType(typeof(PlatformRefreshToken))` maps to table `"PlatformRefreshTokens"` with required `TokenHash` (`GetMaxLength() == 128`, unique index `IX_PlatformRefreshTokens_TokenHash`) and index `IX_PlatformRefreshTokens_UserId`.
- Update `CatalogAndTenantMigrations_ExistSeparately_AndDoNotCrossContaminate` to assert `CatalogDbContext` migrations contain both `InitialCatalog` and `AddPlatformIdentity`, while `AppDbContext` migrations contain only `InitialTenant`.

In `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`, add:
- `AddInfrastructure_RegistersDualIdentity_ResolvingPlatformManagersWithoutActiveTenantContext`: builds service provider from `AddInfrastructure`, creates a scope (with no active tenant), resolves `UserManager<PlatformUser>` and `RoleManager<PlatformRole>` (asserting both are non-null and do not throw `InvalidOperationException`), and resolves `UserManager<ApplicationUser>` only when an active tenant is present (Review Focus #1).

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~CatalogDbContextSchemaTests|FullyQualifiedName~DependencyInjectionTests"`
Expected: FAIL (`PlatformUser`, `PlatformRole`, `PlatformRefreshToken` do not exist).

- [ ] **Step 3: Implement `PlatformRoles`, `PlatformUser`, `PlatformRole`, `PlatformRefreshToken`, upgrade `CatalogDbContext`, register Dual `AddIdentityCore`, and generate `AddPlatformIdentity` migration**

- Create `PlatformRoles`, `PlatformUser`, `PlatformRole`, and `PlatformRefreshToken` per Spec §2.1 and §3.2.
- Update `CatalogDbContext` to inherit `IdentityDbContext<PlatformUser, PlatformRole, Guid>(options)`, keeping `base.OnModelCreating(builder)` first, configuring `Tenant` as before, and configuring `PlatformRefreshToken` per Spec §2.2 (`ToTable("PlatformRefreshTokens")`, `HasIndex(rt => rt.TokenHash).IsUnique().HasDatabaseName("IX_PlatformRefreshTokens_TokenHash")`, `HasIndex(rt => rt.UserId).HasDatabaseName("IX_PlatformRefreshTokens_UserId")`).
- In `DependencyInjection.cs`, register `services.AddIdentityCore<PlatformUser>(...).AddRoles<PlatformRole>().AddEntityFrameworkStores<CatalogDbContext>().AddDefaultTokenProviders()` alongside `AddIdentityCore<ApplicationUser>`.
- Generate Catalog migration:
  `dotnet ef migrations add AddPlatformIdentity --context CatalogDbContext --project apps/backend/src/ExamPrism.csproj --output-dir Infrastructure/Persistence/Migrations/Catalog`

- [ ] **Step 4: Run `CatalogDbContextSchemaTests`, `DependencyInjectionTests`, and `ArchitectureTests` to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~CatalogDbContextSchemaTests|FullyQualifiedName~DependencyInjectionTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Security/PlatformRoles.cs apps/backend/src/Infrastructure/Identity/PlatformUser.cs apps/backend/src/Infrastructure/Identity/PlatformRole.cs apps/backend/src/Infrastructure/Identity/PlatformRefreshToken.cs apps/backend/src/Infrastructure/Persistence/Catalog/CatalogDbContext.cs apps/backend/src/Infrastructure/Persistence/Migrations/Catalog apps/backend/src/Infrastructure/DependencyInjection.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/CatalogDbContextSchemaTests.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs
git commit -m "feat(identity): add PlatformUser, PlatformRole, PlatformRefreshToken, and Catalog Identity migration"
```

---

### Task 2: Platform JWT Claims, Two-Way `JwtTenantValidator`, `"PlatformAdmin"` Policy & `PlatformAdminSeeder`

**Files:**
- Modify: `apps/backend/src/Common/Security/CustomClaimTypes.cs`
- Modify: `apps/backend/src/Infrastructure/Identity/ITokenService.cs`
- Modify: `apps/backend/src/Infrastructure/Identity/TokenService.cs`
- Modify: `apps/backend/src/Infrastructure/Identity/JwtTenantValidator.cs`
- Create: `apps/backend/src/Infrastructure/Identity/PlatformAdminOptions.cs`
- Create: `apps/backend/src/Infrastructure/Identity/PlatformAdminSeeder.cs`
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs`
- Modify: `apps/backend/src/appsettings.json`
- Modify: `apps/backend/src/appsettings.Development.json`
- Modify: `apps/backend/src/Program.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/MultiTenantTestDatabaseHelper.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Identity/TokenServiceTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CrossCuttingTenantIsolationUnitTests.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Identity/PlatformAdminSeederTests.cs`

**Interfaces:**
- Consumes: `PlatformUser`, `PlatformRole`, `PlatformRoles`, `CatalogDbContext`, `ITenantContext`
- Produces:
  - `CustomClaimTypes.TokenScope = "token_scope"`, `CustomClaimTypes.PlatformScopeValue = "platform"`
  - `ITokenService.GeneratePlatformAccessToken(PlatformUser user, IEnumerable<string> roles)`
  - Two-way `JwtTenantValidator.ValidateAsync(TokenValidatedContext context)` enforcing `token_scope == "platform"` (and no `tenant_id`) on Platform hosts, and `tenant_id == tenantInfo.Id` (and no `token_scope == "platform"`) on Tenant hosts
  - `PlatformAdminOptions` (`SectionName = "PlatformAdmin"`, `SeedOnStartup`, `Email`, `Password`)
  - `PlatformAdminSeeder(RoleManager<PlatformRole> roleManager, UserManager<PlatformUser> userManager, IOptions<PlatformAdminOptions> options, ILogger<PlatformAdminSeeder> logger)` (`Task SeedAsync(CancellationToken ct = default)`)
  - Authorization policy `PlatformRoles.PolicyName` (`"PlatformAdmin"`)

- [ ] **Step 1: Write failing unit tests in `TokenServiceTests.cs`, `CrossCuttingTenantIsolationUnitTests.cs`, and `PlatformAdminSeederTests.cs`**

- In `TokenServiceTests.cs`, add:
  - `GeneratePlatformAccessToken_WhenInPlatformContext_EmitsTokenScopeAndRoleClaims_WithoutTenantId`: verifies `token_scope == "platform"`, `ClaimTypes.Role == "SuperAdmin"`, `sub == user.Id.ToString()`, and absence of `"tenant_id"`.
  - `GeneratePlatformAccessToken_WhenActiveTenantPresent_ThrowsInvalidOperationException`: sets `tenantContext.HasTenant.Returns(true)`, asserts `GeneratePlatformAccessToken` throws `InvalidOperationException`.
- In `CrossCuttingTenantIsolationUnitTests.cs`, update `JwtTenantValidator_AllowsMatchingTenantClaim_RejectsCrossTenantOrMissingClaimOnTenantHost_AndRejectsTenantTokenOnPlatformHost` (Review Focus #2):
  - Case 5: Platform host + token with `token_scope == "platform"` (no `tenant_id`) -> succeeds (`context.Result?.Failure` is `null`).
  - Case 6: Platform host + token with neither `tenant_id` nor `token_scope == "platform"` -> fails with `TENANT_TOKEN_MISMATCH`.
  - Case 7: Tenant host + token carrying `token_scope == "platform"` (even if `tenant_id` is forged) -> fails with `TENANT_TOKEN_MISMATCH`.
- Create `apps/backend/tests/ExamPrism.Tests/Identity/PlatformAdminSeederTests.cs` (`namespace Tests.Identity`):
  - `SeedAsync_CreatesSuperAdminRoleAndUser_AndIsIdempotentOnRepeatedCalls`: uses mocked or in-memory/Testcontainers `RoleManager<PlatformRole>` and `UserManager<PlatformUser>`, runs `SeedAsync` twice with `Email = "admin@examprism.com"`, `Password = "AdminPassword123!"`, verifies role `"SuperAdmin"` and user `"admin@examprism.com"` are created once and assigned `"SuperAdmin"`.
  - `SeedAsync_WhenEmailOrPasswordBlank_EnsuresRoleOnlyWithoutCreatingUser`: verifies role `"SuperAdmin"` is created and `userManager.CreateAsync` is not called.

- [ ] **Step 2: Run unit tests to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TokenServiceTests|FullyQualifiedName~CrossCuttingTenantIsolationUnitTests|FullyQualifiedName~PlatformAdminSeederTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `CustomClaimTypes`, `TokenService.GeneratePlatformAccessToken`, `JwtTenantValidator`, `PlatformAdminOptions`, `PlatformAdminSeeder`, and startup wiring**

- Update `CustomClaimTypes`, `ITokenService`, `TokenService`, and `JwtTenantValidator` per Spec §4.1–§4.3.
- Implement `PlatformAdminOptions` and `PlatformAdminSeeder` per Spec §3.2.
- Register `PlatformAdminOptions`, `PlatformAdminSeeder` (`Scoped`), and authorization policy `PlatformRoles.PolicyName` in `DependencyInjection.cs` per Spec §3.2 & §4.4.
- Add `"PlatformAdmin"` section to `appsettings.json` (`SeedOnStartup: false`) and `appsettings.Development.json` (`SeedOnStartup: true`, `Email: "admin@examprism.com"`, `Password: "AdminPassword123!"`) per Spec §3.1.
- In `Program.cs`, before `app.Run()`, check `app.Services.GetRequiredService<IOptions<PlatformAdminOptions>>().Value.SeedOnStartup`; when `true`, create an async scope and call `await scope.ServiceProvider.GetRequiredService<PlatformAdminSeeder>().SeedAsync()`.
- In `CustomWebApplicationFactory.ConfigureWebHost` (Review Focus #3), add:
  ```csharp
  builder.UseSetting("PlatformAdmin:SeedOnStartup", "false");
  builder.UseSetting("PlatformAdmin:Email", "admin@examprism.com");
  builder.UseSetting("PlatformAdmin:Password", "AdminPassword123!");
  ```
  and in `MultiTenantTestDatabaseHelper.InitializeDatabasesAndSeedAsync`, immediately after `await catalogDb.Database.MigrateAsync();`, resolve `PlatformAdminSeeder` and call `await seeder.SeedAsync();`.

- [ ] **Step 4: Run unit tests to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TokenServiceTests|FullyQualifiedName~CrossCuttingTenantIsolationUnitTests|FullyQualifiedName~PlatformAdminSeederTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Security/CustomClaimTypes.cs apps/backend/src/Infrastructure/Identity/ITokenService.cs apps/backend/src/Infrastructure/Identity/TokenService.cs apps/backend/src/Infrastructure/Identity/JwtTenantValidator.cs apps/backend/src/Infrastructure/Identity/PlatformAdminOptions.cs apps/backend/src/Infrastructure/Identity/PlatformAdminSeeder.cs apps/backend/src/Infrastructure/DependencyInjection.cs apps/backend/src/appsettings.json apps/backend/src/appsettings.Development.json apps/backend/src/Program.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/MultiTenantTestDatabaseHelper.cs apps/backend/tests/ExamPrism.Tests/Identity/TokenServiceTests.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/CrossCuttingTenantIsolationUnitTests.cs apps/backend/tests/ExamPrism.Tests/Identity/PlatformAdminSeederTests.cs
git commit -m "feat(identity): add platform JWT scope isolation, PlatformAdmin policy, and SuperAdmin seeder"
```

---

### Task 3: Platform Authentication Vertical Slices (`/api/platform/auth/login`, `/refresh`, `/logout`, `/me`)

**Files:**
- Create: `apps/backend/src/Features/Platform/Auth/PlatformLoginHandler.cs`
- Create: `apps/backend/src/Features/Platform/Auth/PlatformRefreshTokenHandler.cs`
- Create: `apps/backend/src/Features/Platform/Auth/PlatformLogoutHandler.cs`
- Create: `apps/backend/src/Features/Platform/Auth/GetPlatformMeHandler.cs`
- Create: `apps/backend/src/Endpoints/Platform/Auth/PlatformLoginEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Platform/Auth/PlatformRefreshEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Platform/Auth/PlatformLogoutEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Platform/Auth/GetPlatformMeEndpoint.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Features/PlatformAuthHandlersTests.cs`

**Interfaces:**
- Consumes: `UserManager<PlatformUser>`, `CatalogDbContext`, `ITokenService`, `IUserContext`, `JwtOptions`, `AuthCookieHelper`, `PlatformRoles`, `RateLimitPolicies`
- Produces:
  - `ExamPrism.Features.Platform.Auth.PlatformLoginCommand(string Email, string Password)`, `PlatformLoginCommandValidator`, `PlatformLoginResult(Guid Id, string Email, IReadOnlyList<string> Roles, [property: JsonIgnore] string AccessToken = "", [property: JsonIgnore] string RefreshToken = "")`, `PlatformLoginHandler : IHandler<PlatformLoginCommand, PlatformLoginResult>`
  - `ExamPrism.Features.Platform.Auth.PlatformRefreshTokenCommand(string RefreshToken)`, `PlatformRefreshTokenResult([property: JsonIgnore] string AccessToken = "", [property: JsonIgnore] string RefreshToken = "")`, `PlatformRefreshTokenHandler : IHandler<PlatformRefreshTokenCommand, PlatformRefreshTokenResult>`
  - `ExamPrism.Features.Platform.Auth.PlatformLogoutCommand(string? RefreshToken)`, `PlatformLogoutHandler : IHandler<PlatformLogoutCommand>`
  - `ExamPrism.Features.Platform.Auth.GetPlatformMeQuery`, `GetPlatformMeResult(Guid Id, string Email, IReadOnlyList<string> Roles)`, `GetPlatformMeHandler : IHandler<GetPlatformMeQuery, GetPlatformMeResult>`
  - Endpoints: `PlatformLoginEndpoint` (`POST /api/platform/auth/login`), `PlatformRefreshEndpoint` (`POST /api/platform/auth/refresh`), `PlatformLogoutEndpoint` (`POST /api/platform/auth/logout`), `GetPlatformMeEndpoint` (`GET /api/platform/auth/me`)

- [ ] **Step 1: Write failing unit tests in `apps/backend/tests/ExamPrism.Tests/Features/PlatformAuthHandlersTests.cs`**

Create `PlatformAuthHandlersTests` (`namespace Tests.Features`) testing:
- `PlatformLoginHandler_WithValidCredentials_PersistsPlatformRefreshTokenAndReturnsRolesAndTokens`: verifies `PlatformLoginResult` contains `Id`, `Email`, `Roles = ["SuperAdmin"]`, `AccessToken`, `RefreshToken`, and saves a `PlatformRefreshToken` row in `CatalogDbContext`.
- `PlatformLoginHandler_WithInvalidEmailOrPassword_ReturnsUnauthorizedInvalidCredentials`: asserts `result.Error.Code == "INVALID_CREDENTIALS"`.
- `PlatformRefreshTokenHandler_RotatesActiveToken_AndRevokesAllUserTokensOnCompromisedReuse`: verifies active token rotation sets `RevokedAt` + `ReplacedByTokenHash` and inserts a replacement row; replaying the already-revoked token revokes all active tokens for that `PlatformUser` and returns `Error.Code == "COMPROMISED_REFRESH_TOKEN"`.
- `PlatformLogoutHandler_RevokesActiveRefreshToken_AndSucceedsWhenTokenMissing`: verifies `RevokedAt` is set on active token and missing/blank token returns `Result.Success()`.
- `GetPlatformMeHandler_ReturnsAuthenticatedUserWithRoles_OrUnauthorizedWhenUnauthenticated`: verifies `GetPlatformMeResult` returns `Id`, `Email`, `Roles`.

- [ ] **Step 2: Run `PlatformAuthHandlersTests` to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~PlatformAuthHandlersTests"`
Expected: FAIL (handlers do not exist).

- [ ] **Step 3: Implement Platform Auth handlers and endpoints**

Implement all 4 handlers in `ExamPrism.Features.Platform.Auth` and all 4 endpoints in `ExamPrism.Endpoints.Platform.Auth` per Spec §5.1–§5.4:
- Ensure all handler constructors have **no default parameter values** and `Handle(TMessage, CancellationToken ct = default)` returns `Task<Result<TResult>>` or `Task<Result>`.
- Ensure all endpoints are `public sealed`, execute inline via `IMessageBus` without referencing `CatalogDbContext`, and call `.RequirePlatform()`.

- [ ] **Step 4: Run `PlatformAuthHandlersTests` and `ArchitectureTests` to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~PlatformAuthHandlersTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Features/Platform/Auth apps/backend/src/Endpoints/Platform/Auth apps/backend/tests/ExamPrism.Tests/Features/PlatformAuthHandlersTests.cs
git commit -m "feat(platform-auth): add platform login, refresh, logout, and me vertical slices"
```

---

### Task 4: Admin Tenant Management Vertical Slices (`/api/platform/tenants/*`), Tenant Cache Eviction & End-to-End Integration Tests

**Files:**
- Modify: `apps/backend/src/Common/Localization/Resources/SharedResources.resx`
- Modify: `apps/backend/src/Common/Localization/Resources/SharedResources.vi.resx`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs:279-306`
- Create: `apps/backend/src/Features/Platform/Tenants/TenantDtos.cs`
- Create: `apps/backend/src/Features/Platform/Tenants/ListTenantsHandler.cs`
- Create: `apps/backend/src/Features/Platform/Tenants/GetTenantByIdHandler.cs`
- Create: `apps/backend/src/Features/Platform/Tenants/UpdateTenantHandler.cs`
- Create: `apps/backend/src/Features/Platform/Tenants/SuspendTenantHandler.cs`
- Create: `apps/backend/src/Features/Platform/Tenants/ReactivateTenantHandler.cs`
- Create: `apps/backend/src/Endpoints/Platform/Tenants/ListTenantsEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Platform/Tenants/GetTenantByIdEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Platform/Tenants/UpdateTenantEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Platform/Tenants/SuspendTenantEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Platform/Tenants/ReactivateTenantEndpoint.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Features/PlatformTenantHandlersTests.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/PlatformAdminIntegrationTests.cs`

**Interfaces:**
- Consumes: `CatalogDbContext`, `ITenantCacheInvalidator`, `PlatformRoles.PolicyName`, `TenantStatus`
- Produces:
  - `ExamPrism.Features.Platform.Tenants.TenantSummaryItem(Guid Id, string Slug, string Name, TenantStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? ProvisionedAt, DateTimeOffset UpdatedAt)`
  - `ExamPrism.Features.Platform.Tenants.TenantDetailResult(Guid Id, string Slug, string Name, TenantStatus Status, string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? ProvisionedAt, DateTimeOffset UpdatedAt)`
  - `ListTenantsQuery(int Page = 1, int PageSize = 20, TenantStatus? Status = null, string? Search = null)`, `ListTenantsQueryValidator`, `ListTenantsResult(IReadOnlyList<TenantSummaryItem> Items, int TotalCount, int Page, int PageSize)`, `ListTenantsHandler`
  - `GetTenantByIdQuery(Guid Id)`, `GetTenantByIdHandler`
  - `UpdateTenantRequest(string Name)`, `UpdateTenantRequestValidator`, `UpdateTenantCommand(Guid Id, string Name)`, `UpdateTenantCommandValidator`, `UpdateTenantHandler`
  - `SuspendTenantCommand(Guid Id)`, `SuspendTenantHandler`
  - `ReactivateTenantCommand(Guid Id)`, `ReactivateTenantHandler`
  - Endpoints: `ListTenantsEndpoint` (`GET /api/platform/tenants`), `GetTenantByIdEndpoint` (`GET /api/platform/tenants/{id:guid}`), `UpdateTenantEndpoint` (`PATCH /api/platform/tenants/{id:guid}`), `SuspendTenantEndpoint` (`POST /api/platform/tenants/{id:guid}/suspend`), `ReactivateTenantEndpoint` (`POST /api/platform/tenants/{id:guid}/reactivate`)

- [ ] **Step 1: Write failing unit tests in `PlatformTenantHandlersTests.cs` and integration tests in `PlatformAdminIntegrationTests.cs`**

Create `apps/backend/tests/ExamPrism.Tests/Features/PlatformTenantHandlersTests.cs` (`namespace Tests.Features`) testing:
- `ListTenantsHandler_FiltersByStatusAndSearch_AndPaginatesOrderedByCreatedAtDesc`: seeds 4 tenants across statuses, verifies `Status` filter, case-insensitive `Search` on `Slug`/`Name`, and `Page`/`PageSize` slicing.
- `GetTenantByIdHandler_ReturnsDetailWhenFound_AndNotFoundWhenMissing`: verifies `TenantDetailResult` fields and `TENANT_NOT_FOUND` on unknown ID.
- `UpdateTenantHandler_TrimsAndUpdatesName_AndInvalidatesCache`: verifies `Name` and `UpdatedAt` update in `CatalogDbContext` and `cacheInvalidator.InvalidateTenantAsync(id, slug)` is called.
- `SuspendTenantHandler_TransitionsActiveToSuspended_InvalidatesCache_AndRejectsNonActiveTenants` (Review Focus #4): verifies `Active -> Suspended` succeeds and calls `InvalidateTenantAsync`, while `Provisioning`, `Suspended`, or `Failed` returns `409` with `Error.Code == "INVALID_TENANT_STATUS_TRANSITION"`.
- `ReactivateTenantHandler_TransitionsSuspendedWithConnectionStringToActive_InvalidatesCache_AndRejectsInvalidStates` (Review Focus #4): verifies `Suspended` (with `EncryptedConnectionString`) -> `Active` succeeds and calls `InvalidateTenantAsync`, while `Active`, `Provisioning`, or `Suspended` with null/blank `EncryptedConnectionString` returns `409` with `Error.Code == "INVALID_TENANT_STATUS_TRANSITION"`.

Create `apps/backend/tests/ExamPrism.Tests/Infrastructure/PlatformAdminIntegrationTests.cs` (`namespace Tests.Infrastructure`) implementing `IClassFixture<CustomWebApplicationFactory>` and testing Spec §8.2:
- `PlatformAuthFlow_SuperAdminCanLoginGetMeRefreshAndLogout_OnPlatformHost`: exercises `POST /api/platform/auth/login` (`admin@examprism.com` / `AdminPassword123!`), `GET /api/platform/auth/me` (`Roles` contains `"SuperAdmin"`), `POST /api/platform/auth/refresh`, and `POST /api/platform/auth/logout` (`204`).
- `CrossBoundaryAndCrossTokenIsolation_RejectsPlatformRoutesOnTenantHost_TenantTokenOnPlatform_AndPlatformTokenOnTenant` (Review Focus #2):
  - `CreateTenantClient("tenant-a")` calling `POST /api/platform/auth/login` or `GET /api/platform/tenants` -> `404 PLATFORM_ONLY`.
  - Tenant A user token calling `CreatePlatformClient()` `GET /api/platform/tenants` -> `401 TENANT_TOKEN_MISMATCH`.
  - SuperAdmin token calling `CreateTenantClient("tenant-a")` `GET /api/auth/me` -> `401 TENANT_TOKEN_MISMATCH`.
- `AdminTenantLifecycle_ListInspectUpdateSuspendAndReactivate_EvictsCacheImmediately` (Review Focus #5):
  - Registers/logs in a user on `tenant-a.localhost` and confirms `GET /api/auth/me` returns `200 OK`.
  - SuperAdmin on `localhost` calls `GET /api/platform/tenants?search=tenant-a`, `GET /api/platform/tenants/{tenantAId}`, and `PATCH /api/platform/tenants/{tenantAId}` (`Name = "Tenant A Updated"`).
  - SuperAdmin calls `POST /api/platform/tenants/{tenantAId}/suspend` -> `200 OK` (`Status == TenantStatus.Suspended`).
  - Immediately calls `GET http://tenant-a.localhost/api/auth/me` with the tenant user's valid token -> returns `403 Forbidden` with `errorCode == "TENANT_SUSPENDED"`.
  - SuperAdmin calls `POST /api/platform/tenants/{tenantAId}/reactivate` -> `200 OK` (`Status == TenantStatus.Active`).
  - Immediately calls `GET http://tenant-a.localhost/api/auth/me` -> returns `200 OK`.

- [ ] **Step 2: Run `PlatformTenantHandlersTests` to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~PlatformTenantHandlersTests"`
Expected: FAIL.

- [ ] **Step 3: Add localization keys, update `CatalogMultiTenantStore.InvalidateTenantAsync`, and implement Admin Tenant Management handlers and endpoints**

- Add `InvalidTenantStatusTransition`, `InvalidPage`, and `InvalidPageSize` to `SharedResources.resx` and `SharedResources.vi.resx` per Spec §7.
- In `CatalogMultiTenantStore.InvalidateTenantAsync`, when `tenantId != Guid.Empty`, also add `$"t:{tenantId:D}"` to `tags` so `RemoveByTagAsync` evicts the tenant's `HybridCacheService` umbrella tag (Spec §6.1).
- Implement `TenantDtos.cs`, `ListTenantsHandler.cs`, `GetTenantByIdHandler.cs`, `UpdateTenantHandler.cs`, `SuspendTenantHandler.cs`, `ReactivateTenantHandler.cs` in `ExamPrism.Features.Platform.Tenants` and their corresponding endpoints in `ExamPrism.Endpoints.Platform.Tenants` per Spec §6.2.

- [ ] **Step 4: Run the full test suite to verify all unit, architecture, and integration tests pass**

Run: `dotnet test apps/backend/ExamPrism.slnx`
Expected: PASS (0 failures).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Localization/Resources apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs apps/backend/src/Features/Platform/Tenants apps/backend/src/Endpoints/Platform/Tenants apps/backend/tests/ExamPrism.Tests/Features/PlatformTenantHandlersTests.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/PlatformAdminIntegrationTests.cs
git commit -m "feat(platform-tenants): add admin tenant management APIs, instant cache eviction, and integration suite"
```
