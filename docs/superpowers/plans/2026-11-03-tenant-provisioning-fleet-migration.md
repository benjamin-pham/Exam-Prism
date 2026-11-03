# Async Self-Service Provisioning & Fleet Migration Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver Sub-project 3 of ExamPrism's multi-tenant SaaS architecture — public self-service and admin tenant onboarding APIs (`check-slug`, `POST /api/tenants`, `GET /api/tenants/{slug}/status`, `POST /api/platform/tenants`, `retry-provisioning`), idempotent background Wolverine database provisioning (`CREATE DATABASE`, optional dedicated PostgreSQL role creation, EF Core `MigrateAsync`, `TenantOwner` seeding, automatic secret wiping, and startup recovery), and a bounded-parallelism Fleet Migration pipeline (`ITenantFleetMigrator` + Admin APIs) for >500 physical tenant databases.

**Architecture:** `Tenant` in `CatalogDbContext` gains nullable `OwnerEmail` and `EncryptedOwnerPassword` (encrypted via `IConnectionStringProtector` while `Provisioning`/`Failed` and wiped to `null` immediately upon reaching `Active`). Onboarding handlers insert the `Provisioning` catalog record, evict negative cache entries via `ITenantCacheInvalidator`, and publish `StartTenantProvisioningCommand(Guid TenantId)` to Wolverine's local queue (`IMessageBus.SendAsync`). `TenantProvisioner` (in `ExamPrism.Infrastructure.MultiTenancy`) creates the physical PostgreSQL database (and optional dedicated PostgreSQL login role), applies `AppDbContext` migrations, seeds `TenantRoles.TenantOwner` and the initial `ApplicationUser`, encrypts the tenant connection string, clears `EncryptedOwnerPassword`, and evicts caches. `TenantFleetMigrator` executes `AppDbContext.Database.MigrateAsync()` across all `Active` tenants using `Parallel.ForEachAsync` (`MaxDegreeOfParallelism = 10`) with per-tenant fault isolation.

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core 10 Minimal APIs (`IEndpoint`), ASP.NET Core Identity, EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, `Finbuckle.MultiTenant` 10.1.5, `Microsoft.Extensions.Caching.Hybrid` 10.0.0, WolverineFx 4.8.0, FluentValidation, xUnit, FluentAssertions, NSubstitute, `Testcontainers.PostgreSql` 4.16.0.

**Spec:** `docs/superpowers/specs/2026-11-03-tenant-provisioning-fleet-migration-design.md`

## Global Constraints

- Target `.NET 10` (`net10.0`) with zero new NuGet packages required.
- Preserve all `ArchitectureTests` invariants:
  - Every handler in `ExamPrism.Features.*` must be `public sealed`, end with `Handler`, implement `IHandler<TRequest>` or `IHandler<TRequest, TResponse>`, declare a public constructor with **no default parameter values**, declare a single public instance `Handle` method whose first parameter is a `record` ending with `Command`, `Query`, or `Event`, and return `Task<Result>` or `Task<Result<TResult>>` where `TResult` ends with `Result`.
  - `ExamPrism.Features.*` must have zero references to `Microsoft.AspNetCore.{Http,Routing,Mvc}`.
  - Every endpoint in `ExamPrism.Endpoints.*` must be `public sealed`, end with `Endpoint` or `Endpoints`, implement `IEndpoint`, execute inline inside `MapEndpoint` without `Handle`/`HandleAsync` methods, and never reference `DbContext` (`AppDbContext` or `CatalogDbContext`).
- Keep Catalog and Tenant EF Core migrations strictly separate: `AddTenantProvisioningMetadata` must be generated under `Infrastructure/Persistence/Migrations/Catalog` (`--context CatalogDbContext`) without touching `Infrastructure/Persistence/Migrations/Tenant`.
- Never expose `Tenant.EncryptedConnectionString`, `Tenant.EncryptedOwnerPassword`, or internal `FailureReason` on public endpoints (`/api/tenants/*`).
- All public onboarding endpoints (`/api/tenants/*`) and admin platform endpoints (`/api/platform/tenants/*`) must enforce `.RequirePlatform()`, and admin platform endpoints must enforce `.RequireAuthorization(PlatformRoles.PolicyName)`.

## Review Focus

1. **`RecoverStuckProvisioningOnStartup` and `MigrateOnStartup` running in `Program.cs` before `CustomWebApplicationFactory` seeds test databases, or mutating the seeded `"provisioning-org"` tenant used by `503 TENANT_PROVISIONING` guard tests**: `CustomWebApplicationFactory.ConfigureWebHost` must set `MultiTenancy:Provisioning:RecoverStuckProvisioningOnStartup = "false"` and `MultiTenancy:FleetMigration:MigrateOnStartup = "false"` so existing `MultiTenancyIntegrationTests` stay deterministic (pinned in Task 2 & Task 4).
2. **PostgreSQL 17 `public` schema DDL permission failure when `CreateDedicatedPostgresUser == true`**: In PostgreSQL 15+ (`postgres:17-alpine`), a dedicated role cannot create tables in `public` unless it owns the target database. `TenantProvisioner` must create the database with `CREATE DATABASE "{dbName}" OWNER "{roleName}";` (and run `ALTER DATABASE "{dbName}" OWNER TO "{roleName}";` if the database already existed from a prior failed attempt) before executing `AppDbContext.Database.MigrateAsync()` with the dedicated user's connection string (pinned in Task 2 & Task 4).
3. **Negative cache entry (`Exists == false`) in `CatalogMultiTenantStore` surviving after a prior `404` probe to `{slug}.localhost`**: Both `CreateTenantHandler` (upon inserting the `Provisioning` row) and `TenantProvisioner` (upon transitioning to `Active` or `Failed`) must call `ITenantCacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug)` so requests to `{slug}.localhost` immediately transition `404 -> 503 (Provisioning) -> 200 (Active)` without waiting for cache TTL expiry (pinned in Task 3 & Task 4).
4. **Weak `OwnerPassword` passing `POST /api/tenants` only to fail asynchronously inside `UserManager.CreateAsync` during background provisioning**: `CreateTenantCommandValidator` and `CreateTenantHandler` must enforce ASP.NET Core Identity's configured password requirements (minimum length 6, at least one digit `[0-9]`, at least one lowercase letter `[a-z]`) synchronously and return `400 Bad Request` before inserting the `Tenant` record or enqueuing `StartTenantProvisioningCommand` (pinned in Task 3).
5. **Encrypted owner password lingering in `CatalogDbContext.Tenants` after provisioning succeeds, or being wiped prematurely on failure before retry**: `TenantProvisioner` must preserve `EncryptedOwnerPassword` when catching an exception (`Status = Failed`) so `RetryTenantProvisioningHandler` can seed the Owner on retry, and must set `EncryptedOwnerPassword = null` when transitioning to `TenantStatus.Active` (pinned in Task 2 & Task 4).

---

### Task 1: Catalog Provisioning Schema, Configuration Options, Localization & Contracts

**Files:**
- Create: `apps/backend/src/Common/Security/TenantRoles.cs`
- Create: `apps/backend/src/Common/MultiTenancy/ITenantProvisioner.cs`
- Create: `apps/backend/src/Common/MultiTenancy/ITenantFleetMigrator.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/Catalog/Tenant.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/Catalog/CatalogDbContext.cs:17-56`
- Create: `apps/backend/src/Infrastructure/Persistence/Migrations/Catalog/*_AddTenantProvisioningMetadata.cs` (via `dotnet ef`)
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/MultiTenancyOptions.cs`
- Modify: `apps/backend/src/appsettings.json`
- Modify: `apps/backend/src/appsettings.Development.json`
- Modify: `apps/backend/src/Common/Localization/Resources/SharedResources.resx`
- Modify: `apps/backend/src/Common/Localization/Resources/SharedResources.vi.resx`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CatalogDbContextSchemaTests.cs`

**Interfaces:**
- Consumes: `ExamPrism.Infrastructure.Persistence.Catalog.CatalogDbContext`, `ExamPrism.Infrastructure.MultiTenancy.MultiTenancyOptions`
- Produces:
  - `Common.Security.TenantRoles` (`public const string TenantOwner = "TenantOwner";`)
  - `Common.MultiTenancy.ITenantProvisioner` (`Task ProvisionAsync(Guid tenantId, CancellationToken ct = default)`)
  - `Common.MultiTenancy.TenantMigrationItemResult(Guid TenantId, string Slug, bool Succeeded, int AppliedMigrationsCount, string? ErrorMessage, long DurationMs)`
  - `Common.MultiTenancy.FleetMigrationSummaryResult(int TotalTenants, int SucceededCount, int FailedCount, long TotalDurationMs, IReadOnlyList<TenantMigrationItemResult> Results)`
  - `Common.MultiTenancy.ITenantFleetMigrator` (`Task<TenantMigrationItemResult> MigrateTenantAsync(Guid tenantId, CancellationToken ct = default)`, `Task<FleetMigrationSummaryResult> MigrateAllActiveTenantsAsync(CancellationToken ct = default)`)
  - `Tenant.OwnerEmail` (`string?`, max length 256) and `Tenant.EncryptedOwnerPassword` (`string?`)
  - `MultiTenancyOptions.Provisioning` (`TenantProvisioningOptions`: `ServerConnectionString`, `DatabaseNamePrefix = "examprism_tenant_"`, `DedicatedUserPrefix = "examprism_u_"`, `CreateDedicatedPostgresUser = false`, `RecoverStuckProvisioningOnStartup = true`)
  - `MultiTenancyOptions.FleetMigration` (`TenantFleetMigrationOptions`: `MigrateOnStartup = false`, `MaxDegreeOfParallelism = 10`)

- [ ] **Step 1: Write failing schema tests in `CatalogDbContextSchemaTests.cs`**

In `apps/backend/tests/ExamPrism.Tests/Infrastructure/CatalogDbContextSchemaTests.cs`:
- Update `CatalogDbContext_ModelMetadata_ConfiguresTenantsTableAndIndexesCorrectly` to assert `entityType.FindProperty(nameof(Tenant.OwnerEmail))` is nullable with `GetMaxLength() == 256` and `entityType.FindProperty(nameof(Tenant.EncryptedOwnerPassword))` is nullable.
- Update `CatalogAndTenantMigrations_ExistSeparately_AndDoNotCrossContaminate` to assert `CatalogDbContext` migrations include `AddTenantProvisioningMetadata` alongside `InitialCatalog` and `AddPlatformIdentity`, while `AppDbContext` migrations still contain only `InitialTenant`.

- [ ] **Step 2: Run `CatalogDbContextSchemaTests` to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~CatalogDbContextSchemaTests"`
Expected: FAIL (`Tenant.OwnerEmail` and `Tenant.EncryptedOwnerPassword` do not exist).

- [ ] **Step 3: Implement `TenantRoles`, `ITenantProvisioner`, `ITenantFleetMigrator`, `Tenant` properties, `MultiTenancyOptions`, localization resources, and generate `AddTenantProvisioningMetadata` migration**

- Create `TenantRoles` per Spec §2.2, `ITenantProvisioner` per Spec §4.1, and `ITenantFleetMigrator` (`TenantMigrationItemResult`, `FleetMigrationSummaryResult`, `ITenantFleetMigrator`) per Spec §5.1.
- Add `OwnerEmail` and `EncryptedOwnerPassword` to `Tenant.cs` and configure them in `CatalogDbContext.cs` per Spec §2.1.
- Extend `MultiTenancyOptions.cs`, `appsettings.json`, and `appsettings.Development.json` per Spec §2.3.
- Add `SlugAlreadyTaken` and `InvalidSlug` to `SharedResources.resx` and `SharedResources.vi.resx` per Spec §6.
- Generate the Catalog migration:
  `dotnet ef migrations add AddTenantProvisioningMetadata --context CatalogDbContext --project apps/backend/src/ExamPrism.csproj --output-dir Infrastructure/Persistence/Migrations/Catalog`

- [ ] **Step 4: Run `CatalogDbContextSchemaTests` and `ArchitectureTests` to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~CatalogDbContextSchemaTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Security/TenantRoles.cs apps/backend/src/Common/MultiTenancy/ITenantProvisioner.cs apps/backend/src/Common/MultiTenancy/ITenantFleetMigrator.cs apps/backend/src/Infrastructure/Persistence/Catalog/Tenant.cs apps/backend/src/Infrastructure/Persistence/Catalog/CatalogDbContext.cs apps/backend/src/Infrastructure/Persistence/Migrations/Catalog apps/backend/src/Infrastructure/MultiTenancy/MultiTenancyOptions.cs apps/backend/src/appsettings.json apps/backend/src/appsettings.Development.json apps/backend/src/Common/Localization/Resources/SharedResources.resx apps/backend/src/Common/Localization/Resources/SharedResources.vi.resx apps/backend/tests/ExamPrism.Tests/Infrastructure/CatalogDbContextSchemaTests.cs
git commit -m "feat(multitenancy): add tenant provisioning metadata, fleet migration contracts, and Catalog migration"
```

---

### Task 2: `TenantProvisioner`, `StartTenantProvisioningHandler`, `TenantFleetMigrator` & Startup Hooks

**Files:**
- Create: `apps/backend/src/Infrastructure/MultiTenancy/TenantProvisioner.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/TenantFleetMigrator.cs`
- Create: `apps/backend/src/Features/Tenants/StartTenantProvisioningHandler.cs`
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs`
- Modify: `apps/backend/src/Program.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/TenantProvisionerAndFleetMigratorTests.cs`

**Interfaces:**
- Consumes: `ITenantProvisioner`, `ITenantFleetMigrator`, `CatalogDbContext`, `AppDbContext`, `IConnectionStringProtector`, `ITenantConnectionStringNormalizer`, `ITenantCacheInvalidator`, `MultiTenancyOptions`, `TenantRoles`
- Produces:
  - `ExamPrism.Infrastructure.MultiTenancy.TenantProvisioner : ITenantProvisioner`
  - `ExamPrism.Infrastructure.MultiTenancy.TenantFleetMigrator : ITenantFleetMigrator`
  - `ExamPrism.Features.Tenants.StartTenantProvisioningCommand(Guid TenantId)`
  - `ExamPrism.Features.Tenants.StartTenantProvisioningHandler(ITenantProvisioner provisioner) : IHandler<StartTenantProvisioningCommand>`

- [ ] **Step 1: Write failing unit & DI tests in `DependencyInjectionTests.cs` and `TenantProvisionerAndFleetMigratorTests.cs`**

In `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`, add assertions that `ITenantProvisioner` and `ITenantFleetMigrator` resolve as scoped services from `AddInfrastructure`.

Create `apps/backend/tests/ExamPrism.Tests/Infrastructure/TenantProvisionerAndFleetMigratorTests.cs` testing:
1. `StartTenantProvisioningHandler_Handle_DelegatesToTenantProvisionerAndReturnsSuccess`: mocks `ITenantProvisioner`, invokes `StartTenantProvisioningHandler.Handle(new StartTenantProvisioningCommand(id))`, asserts `ProvisionAsync(id)` was called and `Result.IsSuccess == true`.
2. `TenantProvisioner_WhenTenantDoesNotExistOrIsAlreadyActive_NoOpsGracefully`: uses SQLite in-memory `CatalogDbContext`, verifies `ProvisionAsync` returns without modifying `Active` or `Suspended` tenants.
3. `TenantProvisioner_WhenServerConnectionFails_TransitionsTenantToFailed_PreservesEncryptedOwnerPassword_AndInvalidatesCache` (Review Focus #5): seeds a `Provisioning` tenant with `OwnerEmail = "owner@fail.edu"`, `EncryptedOwnerPassword = protector.Protect("OwnerPass123!")`, and an unreachable `ServerConnectionString = "Host=127.0.0.1;Port=1;Database=postgres;Username=postgres;Password=postgres;Timeout=1"`; calls `ProvisionAsync`; asserts `tenant.Status == TenantStatus.Failed`, `tenant.FailureReason` is non-empty, `tenant.EncryptedOwnerPassword` is still preserved (not null), and `ITenantCacheInvalidator.InvalidateTenantAsync` was called.
4. `TenantFleetMigrator_MigrateTenantAsync_WhenTenantMissingOrMissingConnectionString_ReturnsFailedItemWithoutThrowing`: verifies `MigrateTenantAsync` returns `Succeeded == false` with descriptive `ErrorMessage`.

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TenantProvisionerAndFleetMigratorTests|FullyQualifiedName~DependencyInjectionTests"`
Expected: FAIL (`TenantProvisioner`, `TenantFleetMigrator`, `StartTenantProvisioningHandler` do not exist).

- [ ] **Step 3: Implement `TenantProvisioner`, `TenantFleetMigrator`, `StartTenantProvisioningHandler`, DI registrations, and startup hooks**

- Create `StartTenantProvisioningHandler.cs` (`namespace ExamPrism.Features.Tenants`) implementing `IHandler<StartTenantProvisioningCommand>` per Spec §4.1.
- Create `TenantProvisioner.cs` (`namespace ExamPrism.Infrastructure.MultiTenancy`) implementing `ITenantProvisioner` per Spec §4.2:
  - Resolve fallback server connection string from `IConfiguration.GetConnectionString("CatalogConnection")` when `MultiTenancyOptions.Provisioning.ServerConnectionString` is null/whitespace.
  - When `CreateDedicatedPostgresUser == true`, create/update the role `"{roleName}"` and execute `CREATE DATABASE "{dbName}" OWNER "{roleName}";` (or if database already exists, execute `ALTER DATABASE "{dbName}" OWNER TO "{roleName}";` — Review Focus #2).
  - Run `await tenantDb.Database.MigrateAsync(ct)`.
  - Seed `TenantRoles.TenantOwner` and initial `ApplicationUser` inside a dedicated `IServiceScope` with `IMultiTenantContextSetter` populated.
  - On success: set `Status = TenantStatus.Active`, `EncryptedConnectionString = protector.Protect(rawTenantConnString)`, `EncryptedOwnerPassword = null`, `FailureReason = null`, `ProvisionedAt = now`, `UpdatedAt = now`, save to `CatalogDbContext`, and call `cacheInvalidator.InvalidateTenantAsync` (Review Focus #3 & #5).
  - On exception: log error, set `Status = TenantStatus.Failed`, `FailureReason = Truncate(ex.Message, 1000)`, `UpdatedAt = now` (preserving `EncryptedOwnerPassword`), save to `CatalogDbContext`, and call `cacheInvalidator.InvalidateTenantAsync`.
- Create `TenantFleetMigrator.cs` (`namespace ExamPrism.Infrastructure.MultiTenancy`) implementing `ITenantFleetMigrator` per Spec §5.2 using `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`.
- Register `services.AddScoped<ITenantProvisioner, TenantProvisioner>()` and `services.AddScoped<ITenantFleetMigrator, TenantFleetMigrator>()` in `DependencyInjection.cs`.
- In `Program.cs`, add startup hooks for `MultiTenancyOptions.Provisioning.RecoverStuckProvisioningOnStartup` and `MultiTenancyOptions.FleetMigration.MigrateOnStartup` per Spec §4.3 and §5.4 (wrapped in `try/catch` warning logs like `PlatformAdminSeeder`).
- In `CustomWebApplicationFactory.ConfigureWebHost`, add:
  - `builder.UseSetting("MultiTenancy:Provisioning:RecoverStuckProvisioningOnStartup", "false");`
  - `builder.UseSetting("MultiTenancy:FleetMigration:MigrateOnStartup", "false");` (Review Focus #1).

- [ ] **Step 4: Run unit, DI, and architecture tests to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TenantProvisionerAndFleetMigratorTests|FullyQualifiedName~DependencyInjectionTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/MultiTenancy/TenantProvisioner.cs apps/backend/src/Infrastructure/MultiTenancy/TenantFleetMigrator.cs apps/backend/src/Features/Tenants/StartTenantProvisioningHandler.cs apps/backend/src/Infrastructure/DependencyInjection.cs apps/backend/src/Program.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/TenantProvisionerAndFleetMigratorTests.cs
git commit -m "feat(multitenancy): add TenantProvisioner, TenantFleetMigrator, and Wolverine provisioning handler"
```

---

### Task 3: Public & Admin Tenant Onboarding Vertical Slices (`check-slug`, `POST /api/tenants`, `status`, `POST /api/platform/tenants`, `retry-provisioning`)

**Files:**
- Create: `apps/backend/src/Features/Tenants/CheckTenantSlugHandler.cs`
- Create: `apps/backend/src/Features/Tenants/CreateTenantHandler.cs`
- Create: `apps/backend/src/Features/Tenants/GetTenantProvisioningStatusHandler.cs`
- Create: `apps/backend/src/Features/Platform/Tenants/RetryTenantProvisioningHandler.cs`
- Create: `apps/backend/src/Endpoints/Tenants/CheckTenantSlugEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Tenants/ProvisionTenantPublicEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Tenants/GetTenantProvisioningStatusEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Platform/Tenants/CreatePlatformTenantEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Platform/Tenants/RetryTenantProvisioningEndpoint.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Features/Tenants/TenantOnboardingHandlersTests.cs`

**Interfaces:**
- Consumes: `CatalogDbContext`, `IConnectionStringProtector`, `ITenantCacheInvalidator`, `IMessageBus`, `MultiTenancyOptions`, `StartTenantProvisioningCommand`, `TenantDetailResult`
- Produces:
  - `ExamPrism.Features.Tenants.CheckTenantSlugQuery(string Slug)` / `CheckTenantSlugResult(string Slug, bool IsAvailable, string? ReasonCode)` / `CheckTenantSlugHandler`
  - `ExamPrism.Features.Tenants.CreateTenantCommand(string Slug, string Name, string OwnerEmail, string OwnerPassword)` / `CreateTenantCommandValidator` / `TenantProvisioningAcceptedResult(Guid Id, string Slug, string Name, TenantStatus Status, DateTimeOffset CreatedAt)` / `CreateTenantHandler`
  - `ExamPrism.Features.Tenants.GetTenantProvisioningStatusQuery(string Slug)` / `GetTenantProvisioningStatusResult(string Slug, string Name, TenantStatus Status, DateTimeOffset? ProvisionedAt)` / `GetTenantProvisioningStatusHandler`
  - `ExamPrism.Features.Platform.Tenants.RetryTenantProvisioningCommand(Guid Id)` / `RetryTenantProvisioningHandler`
  - Endpoints:
    - `GET /api/tenants/check-slug/{slug}` (`CheckTenantSlugEndpoint`)
    - `POST /api/tenants` (`ProvisionTenantPublicEndpoint` -> `202 Accepted`)
    - `GET /api/tenants/{slug}/status` (`GetTenantProvisioningStatusEndpoint`)
    - `POST /api/platform/tenants` (`CreatePlatformTenantEndpoint` -> `202 Accepted`)
    - `POST /api/platform/tenants/{id:guid}/retry-provisioning` (`RetryTenantProvisioningEndpoint` -> `202 Accepted`)

- [ ] **Step 1: Write failing unit tests in `TenantOnboardingHandlersTests.cs`**

Create `apps/backend/tests/ExamPrism.Tests/Features/Tenants/TenantOnboardingHandlersTests.cs` testing:
1. `CheckTenantSlugHandler_ReturnsExpectedAvailability_ForValidInvalidReservedAndTakenSlugs`: verifies `IsAvailable = true` for `"sunrise-academy"`, `ReasonCode = "INVALID_SLUG"` for `"_bad_slug"`, `ReasonCode = "RESERVED_SLUG"` for `"admin"`, and `ReasonCode = "SLUG_ALREADY_TAKEN"` for existing `"tenant-a"`.
2. `CreateTenantHandler_WithValidCommand_SavesProvisioningTenantWithEncryptedPassword_InvalidatesCache_AndSendsWolverineCommand` (Review Focus #3 & #5): verifies `Status == TenantStatus.Provisioning`, `EncryptedOwnerPassword` is protected (not plaintext), `ITenantCacheInvalidator.InvalidateTenantAsync` is called, and `IMessageBus.SendAsync(new StartTenantProvisioningCommand(tenant.Id))` is called.
3. `CreateTenantHandler_RejectsDuplicateOrReservedSlug_With409SlugAlreadyTaken`: verifies `Error.Code == "SLUG_ALREADY_TAKEN"` and `Error.Type == ErrorType.Conflict`.
4. `CreateTenantHandler_AndValidator_RejectWeakOwnerPassword_MissingDigitOrLowercaseOrTooShort` (Review Focus #4): verifies password without a digit (`"NoDigitsHere"`) or without lowercase (`"ALLUPPER123"`) or shorter than 6 chars fails synchronously with `400` validation error without inserting a `Tenant` row or publishing `StartTenantProvisioningCommand`.
5. `GetTenantProvisioningStatusHandler_ReturnsPublicStatus_And404WhenUnknown`: verifies status response omits internal failure reasons and returns `404 TENANT_NOT_FOUND` when unknown.
6. `RetryTenantProvisioningHandler_TransitionsFailedToProvisioning_ClearsFailureReason_AndReSendsCommand`: verifies `Failed -> Provisioning` succeeds and dispatches `StartTenantProvisioningCommand`, while `Active` or `Suspended` returns `409 INVALID_TENANT_STATUS_TRANSITION`.

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TenantOnboardingHandlersTests"`
Expected: FAIL (handlers and endpoints do not exist).

- [ ] **Step 3: Implement onboarding handlers, validators, and endpoints**

- Create `CheckTenantSlugHandler.cs`, `CreateTenantHandler.cs`, `GetTenantProvisioningStatusHandler.cs` in `apps/backend/src/Features/Tenants/` and `RetryTenantProvisioningHandler.cs` in `apps/backend/src/Features/Platform/Tenants/` per Spec §3.1–§3.4.
- In `CreateTenantCommandValidator` and `CreateTenantHandler`, enforce password rules matching `DependencyInjection.cs` (`MinimumLength(6)`, contains digit `[0-9]`, contains lowercase `[a-z]`) so weak passwords fail synchronously with `400` (Review Focus #4).
- Create `CheckTenantSlugEndpoint.cs`, `ProvisionTenantPublicEndpoint.cs`, `GetTenantProvisioningStatusEndpoint.cs` in `apps/backend/src/Endpoints/Tenants/` and `CreatePlatformTenantEndpoint.cs`, `RetryTenantProvisioningEndpoint.cs` in `apps/backend/src/Endpoints/Platform/Tenants/` per Spec §3.1–§3.4.

- [ ] **Step 4: Run `TenantOnboardingHandlersTests` and `ArchitectureTests` to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TenantOnboardingHandlersTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Features/Tenants apps/backend/src/Features/Platform/Tenants/RetryTenantProvisioningHandler.cs apps/backend/src/Endpoints/Tenants apps/backend/src/Endpoints/Platform/Tenants/CreatePlatformTenantEndpoint.cs apps/backend/src/Endpoints/Platform/Tenants/RetryTenantProvisioningEndpoint.cs apps/backend/tests/ExamPrism.Tests/Features/Tenants/TenantOnboardingHandlersTests.cs
git commit -m "feat(tenants): add public and admin tenant onboarding, status polling, and retry provisioning slices"
```

---

### Task 4: Admin Fleet Migration Vertical Slices & End-to-End Provisioning/Migration Integration Suite

**Files:**
- Create: `apps/backend/src/Features/Platform/Tenants/MigrateAllTenantsHandler.cs`
- Create: `apps/backend/src/Features/Platform/Tenants/MigrateSingleTenantHandler.cs`
- Create: `apps/backend/src/Endpoints/Platform/Tenants/MigrateAllTenantsEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Platform/Tenants/MigrateSingleTenantEndpoint.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Features/Platform/Tenants/MigrateTenantHandlersTests.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Integration/TenantProvisioningAndFleetMigrationIntegrationTests.cs`

**Interfaces:**
- Consumes: `ITenantFleetMigrator`, `CatalogDbContext`, `PlatformRoles`, `CustomWebApplicationFactory`
- Produces:
  - `ExamPrism.Features.Platform.Tenants.MigrateAllTenantsCommand()` / `MigrateAllTenantsHandler`
  - `ExamPrism.Features.Platform.Tenants.MigrateSingleTenantCommand(Guid Id)` / `MigrateSingleTenantHandler`
  - Endpoints:
    - `POST /api/platform/tenants/migrate` (`MigrateAllTenantsEndpoint`)
    - `POST /api/platform/tenants/{id:guid}/migrate` (`MigrateSingleTenantEndpoint`)

- [ ] **Step 1: Write failing unit tests in `MigrateTenantHandlersTests.cs` and E2E integration tests in `TenantProvisioningAndFleetMigrationIntegrationTests.cs`**

Create `apps/backend/tests/ExamPrism.Tests/Features/Platform/Tenants/MigrateTenantHandlersTests.cs` testing:
1. `MigrateAllTenantsHandler_DelegatesToFleetMigrator_AndReturnsSummary`: asserts `MigrateAllActiveTenantsAsync` result is returned wrapped in `Result<FleetMigrationSummaryResult>`.
2. `MigrateSingleTenantHandler_Returns404WhenMissing_409WhenNotActive_AndDelegatesWhenActive`: asserts `404 TENANT_NOT_FOUND` for unknown ID, `409 INVALID_TENANT_STATUS_TRANSITION` for `Provisioning`/`Failed` tenant without connection string, and `200` with `TenantMigrationItemResult` for `Active` tenant.

Create `apps/backend/tests/ExamPrism.Tests/Integration/TenantProvisioningAndFleetMigrationIntegrationTests.cs` testing against `CustomWebApplicationFactory`:
1. `CheckSlug_PublicEndpoint_ReturnsAvailableForNewSlug_AndUnavailableForReservedOrTakenSlugs`: calls `GET /api/tenants/check-slug/sunrise-academy` (`isAvailable: true`), `GET /api/tenants/check-slug/admin` (`isAvailable: false, reasonCode: "RESERVED_SLUG"`), and `GET /api/tenants/check-slug/tenant-a` (`isAvailable: false, reasonCode: "SLUG_ALREADY_TAKEN"`).
2. `SelfServiceOnboarding_CreatesPhysicalDatabase_MigratesSchema_SeedsTenantOwner_WipesOwnerPassword_AndAllowsImmediateTenantLogin` (Review Focus #3 & #5):
   - First probes `GET http://sunrise-academy.localhost/api/auth/me` -> receives `404 TENANT_NOT_FOUND` (populating negative cache).
   - Calls `POST http://localhost/api/tenants` with `{ slug: "sunrise-academy", name: "Sunrise Academy", ownerEmail: "owner@sunrise.edu", ownerPassword: "OwnerPassword123!" }` -> asserts `202 Accepted`.
   - Polls `GET http://localhost/api/tenants/sunrise-academy/status` (up to 10s with 200ms delay) until `Status == TenantStatus.Active` and `ProvisionedAt != null`.
   - Inspects `CatalogDbContext.Tenants` for `"sunrise-academy"` and asserts `EncryptedConnectionString` is populated while `EncryptedOwnerPassword` is `null`.
   - Calls `POST http://sunrise-academy.localhost/api/auth/login` with `{ email: "owner@sunrise.edu", password: "OwnerPassword123!" }` -> asserts `200 OK` and `GET http://sunrise-academy.localhost/api/auth/me` returns `"owner@sunrise.edu"`.
3. `DedicatedPostgresUserProvisioning_CreatesRoleAndDatabaseOwnedByRole_AndAppliesMigrations` (Review Focus #2):
   - Resolves `CatalogDbContext`, `IConnectionStringProtector`, and configures `MultiTenancyOptions.Provisioning.CreateDedicatedPostgresUser = true` in a test scope for a new tenant `"isolated-org"`, invokes `ITenantProvisioner.ProvisionAsync`, and verifies the dedicated PostgreSQL role `"examprism_u_isolated_org"` exists in `pg_roles`, owns `"examprism_tenant_isolated_org"`, and the seeded owner exists in the new tenant database.
4. `AdminRetryProvisioning_RecoversFailedTenant_ToActiveStatus`:
   - Updates `"failed-org"` in `CatalogDbContext` with `OwnerEmail = "owner@failed-org.edu"` and `EncryptedOwnerPassword = protector.Protect("OwnerPassword123!")`.
   - Logs in as `SuperAdmin` on `http://localhost` and calls `POST /api/platform/tenants/55555555-5555-5555-5555-555555555555/retry-provisioning` -> asserts `202 Accepted`.
   - Polls `GET /api/tenants/failed-org/status` until `Status == TenantStatus.Active`, then logs in on `http://failed-org.localhost/api/auth/login` as `"owner@failed-org.edu"` -> asserts `200 OK`.
5. `FleetMigrationEndpoints_MigrateAllAndSingleActiveTenants_AsSuperAdmin`:
   - Logs in as `SuperAdmin` on `http://localhost`.
   - Calls `POST /api/platform/tenants/migrate` -> asserts `200 OK`, `FailedCount == 0`, `SucceededCount >= 2`.
   - Calls `POST /api/platform/tenants/11111111-1111-1111-1111-111111111111/migrate` -> asserts `200 OK`, `Succeeded == true`.

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~MigrateTenantHandlersTests|FullyQualifiedName~TenantProvisioningAndFleetMigrationIntegrationTests"`
Expected: FAIL (`MigrateAllTenantsHandler` and `MigrateSingleTenantHandler` do not exist).

- [ ] **Step 3: Implement `MigrateAllTenantsHandler`, `MigrateSingleTenantHandler`, and endpoints**

- Create `MigrateAllTenantsHandler.cs` and `MigrateSingleTenantHandler.cs` in `apps/backend/src/Features/Platform/Tenants/` per Spec §5.3.
- Create `MigrateAllTenantsEndpoint.cs` and `MigrateSingleTenantEndpoint.cs` in `apps/backend/src/Endpoints/Platform/Tenants/` per Spec §5.3.

- [ ] **Step 4: Run full test suite (`dotnet test`) to verify all unit, architecture, and integration tests pass**

Run: `dotnet test apps/backend/ExamPrism.slnx`
Expected: PASS (0 failures across the entire solution).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Features/Platform/Tenants/MigrateAllTenantsHandler.cs apps/backend/src/Features/Platform/Tenants/MigrateSingleTenantHandler.cs apps/backend/src/Endpoints/Platform/Tenants/MigrateAllTenantsEndpoint.cs apps/backend/src/Endpoints/Platform/Tenants/MigrateSingleTenantEndpoint.cs apps/backend/tests/ExamPrism.Tests/Features/Platform/Tenants/MigrateTenantHandlersTests.cs apps/backend/tests/ExamPrism.Tests/Integration/TenantProvisioningAndFleetMigrationIntegrationTests.cs
git commit -m "feat(platform-tenants): add fleet migration APIs and E2E provisioning integration suite"
```

