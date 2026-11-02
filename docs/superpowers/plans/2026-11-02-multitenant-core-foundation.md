# Multi-Tenant Core Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver Sub-project 1 of ExamPrism's multi-tenant SaaS architecture — subdomain-based tenant resolution, `HybridCache`-backed catalog lookup with encrypted connection strings, separate `CatalogDbContext` and dynamic per-tenant `AppDbContext` (>500-DB Npgsql pool tuning), lifecycle boundary guards, and cross-cutting tenant isolation across JWT/Cookies, `HybridCacheService`, `RateLimitingSetup`, `/health`, and background Wolverine messages using `Finbuckle.MultiTenant` v10.

**Architecture:** `Common.MultiTenancy` (`TenantStatus`, `ITenantContext`) and `Common.Security.CustomClaimTypes` expose clean, HTTP/Finbuckle-free abstractions to `ExamPrism.Features`. `ExamPrism.Infrastructure.MultiTenancy` integrates `Finbuckle.MultiTenant` (`10.1.5`) via `SubdomainMultiTenantStrategy`, `CatalogMultiTenantStore` (reading `CatalogDbContext.Tenants` through `HybridCache` storing only DataProtection ciphertext plus short-TTL negative sentinels), `TenantAccessGuardMiddleware`, fail-fast dynamic `AppDbContext` with normalized Npgsql pool limits, and `TenantWolverineMiddleware` bridging `Envelope.TenantId` for async jobs.

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core 10 Minimal APIs (`IEndpoint`), `Finbuckle.MultiTenant` 10.1.5, `Finbuckle.MultiTenant.AspNetCore` 10.1.5, EF Core 10 + `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3, ASP.NET Core Identity & Data Protection, `Microsoft.Extensions.Caching.Hybrid` 10.0.0, WolverineFx 4.8.0, xUnit, FluentAssertions, NSubstitute, `Testcontainers.PostgreSql` 4.16.0, `Testcontainers.Redis` 4.16.0.

**Spec:** `docs/superpowers/specs/2026-11-02-multitenant-core-foundation-design.md`

## Global Constraints

- Target `.NET 10` (`net10.0`) with Central Package Management in `apps/backend/Directory.Packages.props`.
- Install `Finbuckle.MultiTenant` `10.1.5` and `Finbuckle.MultiTenant.AspNetCore` `10.1.5` only; do **not** install `Finbuckle.MultiTenant.EntityFrameworkCore` (physical Database-per-Tenant — `AppDbContext` tables must remain free of `TenantId` shadow columns and global query filters).
- Preserve all `ArchitectureTests` invariants: `ExamPrism.Features.*` has zero `Microsoft.AspNetCore.{Http,Routing,Mvc}` or `Finbuckle.*` references; `IEndpoint` implementations execute inline inside `MapEndpoint` without `Handle`/`HandleAsync` methods or `DbContext` references.
- `AppDbContext` must fail fast with `InvalidOperationException` whenever resolved without an active tenant (`Status == TenantStatus.Active` and non-whitespace decrypted `ConnectionString`) and must **never** fall back to `CatalogConnection`.
- Tenant database connection strings must remain DataProtection-encrypted (`purpose: "ExamPrism.TenantConnectionStrings.v1"`) both in `CatalogDbContext.Tenants` and inside `HybridCache` (including Redis L2), decrypted in memory only when materializing `ExamPrismTenantInfo`.
- Every tenant connection string passed to `UseNpgsql` must be normalized via `NpgsqlConnectionStringBuilder` (`Pooling = true`, `MinPoolSize = 0`, `MaxPoolSize = 15`, `ConnectionIdleLifetime = 30`, `ConnectionPruningInterval = 10` by default from `MultiTenancyOptions.DatabasePool`).
- All multi-tenancy error responses (`TENANT_NOT_FOUND` `404`, `TENANT_PROVISIONING` `503` + `Retry-After`, `TENANT_SUSPENDED` `403`, `TENANT_UNAVAILABLE` `403`, `TENANT_REQUIRED` `400`, `PLATFORM_ONLY` `404`, `TENANT_TOKEN_MISMATCH` `401`) must return `application/problem+json` localized via `IStringLocalizer<SharedResources>` (`vi-VN` and `en-US`) with `Extensions["traceId"]` and `Extensions["errorCode"]`.

## Review Focus

1. **Overlapping `BaseDomains` (e.g., `["examprism.com", "staging.examprism.com"]`) plus uppercase, port, or FQDN trailing dot (`SCHOOL-A.Staging.ExamPrism.com.:8443`)**: `SubdomainParser` must normalize host casing, strip trailing `.`, ignore port, and match `BaseDomains` ordered by descending length so `school-a.staging.examprism.com` resolves `TenantCandidate("school-a")` against `staging.examprism.com` rather than failing as a multi-level subdomain of `examprism.com` (pinned in Task 3).
2. **Plaintext PostgreSQL passwords leaking into Redis L2 cache**: `CatalogMultiTenantStore` must serialize `CatalogTenantCacheEntry` with `EncryptedConnectionString` (ciphertext) into `HybridCache`; inspecting raw bytes written to `IDistributedCache` must never contain the plaintext database password (pinned in Task 3).
3. **Non-active tenant (`Provisioning`, `Suspended`, `Failed`) remaining set on `IMultiTenantContextAccessor` after `TenantAccessGuardMiddleware` rejects the request**: `TenantAccessGuardMiddleware` must reset `IMultiTenantContextSetter.MultiTenantContext` before returning `503`/`403` so no exception handler, logger, or downstream component sees an active tenant context (pinned in Task 4).
4. **`X-Tenant` header spoofing overriding an explicit subdomain in Development (`Host: tenant-a.localhost` + `X-Tenant: tenant-b`) or working in Production**: `SubdomainParser` must consult `X-Tenant` **only** when `IsDevelopment() && AllowHeaderFallbackInDevelopment` **and** the incoming `Host` resolved to a root `Platform` base domain without any subdomain (pinned in Task 3).
5. **`AsyncLocal` tenant context leaking across consecutive background messages in `TenantWolverineMiddleware`**: When `TenantWolverineMiddleware` sets `IMultiTenantContextSetter.MultiTenantContext` for a background `Envelope.TenantId`, its `Finally` cleanup must restore the prior `IMultiTenantContext<ExamPrismTenantInfo>` (while leaving pre-existing HTTP contexts untouched) so subsequent untenant work on the same async flow never inherits the previous tenant's database (pinned in Task 5).

---

### Task 1: Finbuckle v10 Packages, Clean Multi-Tenancy Contracts, Encryption & Npgsql Pool Normalizer

**Files:**
- Modify: `apps/backend/Directory.Packages.props:7-24`
- Modify: `apps/backend/src/ExamPrism.csproj:3-22`
- Create: `apps/backend/src/Common/MultiTenancy/TenantStatus.cs`
- Create: `apps/backend/src/Common/MultiTenancy/ITenantContext.cs`
- Create: `apps/backend/src/Common/Security/CustomClaimTypes.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/MultiTenancyOptions.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/ExamPrismTenantInfo.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/TenantContext.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/IConnectionStringProtector.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/DataProtectionConnectionStringProtector.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/ITenantConnectionStringNormalizer.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/TenantConnectionStringNormalizer.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/MultiTenancyPrimitivesTests.cs`

**Interfaces:**
- Consumes: `Finbuckle.MultiTenant.Abstractions.TenantInfo`, `Finbuckle.MultiTenant.Abstractions.IMultiTenantContextAccessor<TTenantInfo>`, `Microsoft.AspNetCore.DataProtection.IDataProtectionProvider`, `Npgsql.NpgsqlConnectionStringBuilder`
- Produces:
  - `Common.MultiTenancy.TenantStatus` (`Provisioning = 0`, `Active = 1`, `Suspended = 2`, `Failed = 3`)
  - `Common.MultiTenancy.ITenantContext` (`bool HasTenant`, `Guid? TenantId`, `string? Identifier`, `string? Name`)
  - `Common.Security.CustomClaimTypes` (`public const string TenantId = "tenant_id";`)
  - `ExamPrism.Infrastructure.MultiTenancy.MultiTenancyOptions` (`SectionName = "MultiTenancy"`, `BaseDomains`, `ReservedSubdomains`, `AllowHeaderFallbackInDevelopment`, `HeaderName`, `CatalogCacheExpirationMinutes`, `CatalogLocalCacheExpirationMinutes`, `NotFoundCacheExpirationSeconds`, `ProvisioningRetryAfterSeconds`, `DatabasePool : TenantDatabasePoolOptions`)
  - `ExamPrism.Infrastructure.MultiTenancy.ExamPrismTenantInfo(string Id, string Identifier) : TenantInfo(Id, Identifier)` with `string? Name { get; init; }`, `Guid TenantGuid { get; init; }`, `TenantStatus Status { get; init; }`, `string? ConnectionString { get; init; }`
  - `ExamPrism.Infrastructure.MultiTenancy.TenantContext(IMultiTenantContextAccessor<ExamPrismTenantInfo> accessor) : ITenantContext`
  - `ExamPrism.Infrastructure.MultiTenancy.IConnectionStringProtector` (`string Protect(string plaintext)`, `string Unprotect(string ciphertext)`) & `DataProtectionConnectionStringProtector(IDataProtectionProvider provider)` (`Purpose = "ExamPrism.TenantConnectionStrings.v1"`)
  - `ExamPrism.Infrastructure.MultiTenancy.ITenantConnectionStringNormalizer` (`string Normalize(string connectionString)`) & `TenantConnectionStringNormalizer(IOptions<MultiTenancyOptions> options)`

- [ ] **Step 1: Add Finbuckle v10 package references**

In `apps/backend/Directory.Packages.props` under `<!-- API Dependencies -->`:
```xml
<PackageVersion Include="Finbuckle.MultiTenant" Version="10.1.5" />
<PackageVersion Include="Finbuckle.MultiTenant.AspNetCore" Version="10.1.5" />
```
In `apps/backend/src/ExamPrism.csproj`, add `<PackageReference Include="Finbuckle.MultiTenant" />` and `<PackageReference Include="Finbuckle.MultiTenant.AspNetCore" />`, then run `dotnet restore apps/backend/ExamPrism.slnx` (bypass sandbox if network restore is required).

- [ ] **Step 2: Write failing unit tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/MultiTenancyPrimitivesTests.cs`**

Create `MultiTenancyPrimitivesTests` (`namespace Tests.Infrastructure`) testing:
- `TenantContext_WhenActiveTenantPresent_ExposesProperties_AndWhenNullOrNonActive_ReportsHasTenantFalse`: tests `Active` tenant (`HasTenant == true`, `TenantId == guid`, `Identifier == "school-a"`, `Name == "School A"`), `null` context (`HasTenant == false`, all nullable properties `null`), and `Suspended` / `Provisioning` / `Failed` tenants (`HasTenant == false`, `TenantId == null`).
- `DataProtectionConnectionStringProtector_RoundTripsSecret_ProducesCiphertextDistinctFromPlaintext_AndRejectsTamperedPayload`: uses `DataProtectionProvider.Create("ExamPrism.Tests")`, protects `"Host=db;Database=t1;Username=postgres;Password=TopSecret123!"`, asserts ciphertext does not contain `"TopSecret123!"`, `Unprotect` returns exact original string, and tampered ciphertext throws `CryptographicException`.
- `TenantConnectionStringNormalizer_AppliesConfiguredPoolLimits_PreservesEndpointCredentials_AndThrowsOnBlankInput`: configures `MaxPoolSize = 15`, `MinPoolSize = 0`, `ConnectionIdleLifetimeSeconds = 30`, `ConnectionPruningIntervalSeconds = 10`, normalizes `"Host=pg.internal;Port=5433;Database=tenant_a;Username=app;Password=pw;MaxPoolSize=100"`, parses output with `NpgsqlConnectionStringBuilder`, asserts `Pooling == true`, `MaxPoolSize == 15`, `MinPoolSize == 0`, `ConnectionIdleLifetime == 30`, `ConnectionPruningInterval == 10`, `Host == "pg.internal"`, `Port == 5433`, `Database == "tenant_a"`.

- [ ] **Step 3: Run `MultiTenancyPrimitivesTests` to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~MultiTenancyPrimitivesTests"`
Expected: FAIL (types do not exist yet).

- [ ] **Step 4: Implement contracts, `ExamPrismTenantInfo`, `TenantContext`, encryption, and connection-string normalizer**

Implement all files listed in **Files** matching Spec §2.3, §3.1, §3.2, §4.3, §4.4:
- `TenantContext`: returns `HasTenant = true` and populated `TenantId`/`Identifier`/`Name` **only** when `accessor.MultiTenantContext.TenantInfo is { Status: TenantStatus.Active } info`; otherwise `false` and `null`.
- `DataProtectionConnectionStringProtector`: creates protector `"ExamPrism.TenantConnectionStrings.v1"`, guards `ArgumentException.ThrowIfNullOrWhiteSpace` on `Protect` and `Unprotect`.
- `TenantConnectionStringNormalizer`: guards `ArgumentException.ThrowIfNullOrWhiteSpace(connectionString)`, applies `Pooling = true`, `MinPoolSize`, `MaxPoolSize`, `ConnectionIdleLifetime`, `ConnectionPruningInterval` via `NpgsqlConnectionStringBuilder`, returns `builder.ConnectionString`.

- [ ] **Step 5: Run `MultiTenancyPrimitivesTests` to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~MultiTenancyPrimitivesTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add apps/backend/Directory.Packages.props apps/backend/src/ExamPrism.csproj apps/backend/src/Common/MultiTenancy apps/backend/src/Common/Security/CustomClaimTypes.cs apps/backend/src/Infrastructure/MultiTenancy apps/backend/tests/ExamPrism.Tests/Infrastructure/MultiTenancyPrimitivesTests.cs
git commit -m "feat(multitenancy): add Finbuckle v10 packages, tenant contracts, encryption, and pool normalizer"
```

---

### Task 2: `CatalogDbContext`, `Tenant` Entity, Design-Time Factories & Separate Catalog / Tenant EF Migrations

**Files:**
- Create: `apps/backend/src/Common/MultiTenancy/TenantSlugValidator.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/Catalog/Tenant.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/Catalog/CatalogDbContext.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/Catalog/CatalogDbContextFactory.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/AppDbContextFactory.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/Migrations/Catalog/*` (generated via `dotnet ef`)
- Create: `apps/backend/src/Infrastructure/Persistence/Migrations/Tenant/*` (generated via `dotnet ef`)
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CatalogDbContextSchemaTests.cs`

**Interfaces:**
- Consumes: `Common.MultiTenancy.TenantStatus`, `ExamPrism.Infrastructure.Persistence.AppDbContext`
- Produces:
  - `Common.MultiTenancy.TenantSlugValidator` (`public const int MaxLength = 63;`, `bool IsValid(string? slug)` matching compiled regex `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`)
  - `ExamPrism.Infrastructure.Persistence.Catalog.Tenant` (`Id`, `Slug`, `Name`, `EncryptedConnectionString`, `Status`, `FailureReason`, `CreatedAt`, `ProvisionedAt`, `UpdatedAt`)
  - `ExamPrism.Infrastructure.Persistence.Catalog.CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)` (`DbSet<Tenant> Tenants`)
  - `CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>` & `AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>`

- [ ] **Step 1: Write failing schema & migration isolation tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/CatalogDbContextSchemaTests.cs`**

Create `CatalogDbContextSchemaTests` (`namespace Tests.Infrastructure`) testing:
- `TenantSlugValidator_AcceptsValidDnsLabels_AndRejectsInvalidLabels`: `[Theory]` testing valid (`"a"`, `"school-a"`, `"org123"`, 63-char lowercase alphanumeric) -> `true`; invalid (`""`, `"School-A"`, `"-abc"`, `"abc-"`, `"a.b"`, `"a_b"`, 64-char string) -> `false`.
- `CatalogDbContext_ModelMetadata_ConfiguresTenantsTableIndexesAndConversions`: builds `CatalogDbContext` with `UseNpgsql("Host=localhost;Database=dummy")`, inspects `context.Model.FindEntityType(typeof(Tenant))`, asserts `Slug` is required with `GetMaxLength() == 63` and unique index `IX_Tenants_Slug`, `Name` required with `GetMaxLength() == 200`, `Status` has string value converter with `GetMaxLength() == 32` and index `IX_Tenants_Status`, `EncryptedConnectionString` is nullable, `FailureReason` has `GetMaxLength() == 1000`.
- `CatalogAndTenantMigrations_ExistSeparately_AndDoNotCrossContaminate`: uses EF `IMigrationsAssembly` on both `CatalogDbContext` and `AppDbContext` to assert each context discovers at least 1 migration (`InitialCatalog` vs `InitialTenant`) and zero migrations are shared between the two contexts.

- [ ] **Step 2: Run `CatalogDbContextSchemaTests` to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~CatalogDbContextSchemaTests"`
Expected: FAIL (`TenantSlugValidator`, `Tenant`, `CatalogDbContext` do not exist).

- [ ] **Step 3: Implement `TenantSlugValidator`, `Tenant`, `CatalogDbContext`, design-time factories, and generate both initial migrations**

- Implement `TenantSlugValidator` (`[GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]`).
- Implement `Tenant` and `CatalogDbContext` per Spec §4.1 (`ToTable("Tenants")`, `HasIndex(t => t.Slug).IsUnique().HasDatabaseName("IX_Tenants_Slug")`, `HasIndex(t => t.Status).HasDatabaseName("IX_Tenants_Status")`, `Property(t => t.Status).HasConversion<string>().HasMaxLength(32)`).
- Implement `CatalogDbContextFactory` and `AppDbContextFactory` pointing design-time `UseNpgsql` to dummy local connection strings.
- Generate baseline migrations:
  - `dotnet ef migrations add InitialCatalog --context CatalogDbContext --project apps/backend/src/ExamPrism.csproj --output-dir Infrastructure/Persistence/Migrations/Catalog`
  - `dotnet ef migrations add InitialTenant --context AppDbContext --project apps/backend/src/ExamPrism.csproj --output-dir Infrastructure/Persistence/Migrations/Tenant`

- [ ] **Step 4: Run `CatalogDbContextSchemaTests` and `ArchitectureTests` to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~CatalogDbContextSchemaTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/MultiTenancy/TenantSlugValidator.cs apps/backend/src/Infrastructure/Persistence/Catalog apps/backend/src/Infrastructure/Persistence/AppDbContextFactory.cs apps/backend/src/Infrastructure/Persistence/Migrations apps/backend/tests/ExamPrism.Tests/Infrastructure/CatalogDbContextSchemaTests.cs
git commit -m "feat(persistence): add CatalogDbContext, Tenant entity, and separate Catalog/Tenant EF migrations"
```

---

### Task 3: `SubdomainParser`, `SubdomainMultiTenantStrategy` & `CatalogMultiTenantStore` (with Ciphertext `HybridCache` & Negative Caching)

**Files:**
- Create: `apps/backend/src/Infrastructure/MultiTenancy/SubdomainParser.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/SubdomainMultiTenantStrategy.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/ITenantCacheInvalidator.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/SubdomainAndCatalogStoreTests.cs`

**Interfaces:**
- Consumes: `MultiTenancyOptions`, `TenantSlugValidator`, `ExamPrismTenantInfo`, `CatalogDbContext`, `IConnectionStringProtector`, `HybridCache`, `Finbuckle.MultiTenant.Abstractions.IMultiTenantStrategy`, `Finbuckle.MultiTenant.Abstractions.IMultiTenantStore<ExamPrismTenantInfo>`
- Produces:
  - `ExamPrism.Infrastructure.MultiTenancy.HostResolutionKind` (`Platform`, `TenantCandidate`, `InvalidTenantHost`)
  - `ExamPrism.Infrastructure.MultiTenancy.HostResolutionResult(HostResolutionKind Kind, string? Identifier)`
  - `ExamPrism.Infrastructure.MultiTenancy.SubdomainParser` (`public const string HttpContextItemKey = "ExamPrism.HostResolution";`, `HostResolutionResult Parse(HttpContext httpContext, MultiTenancyOptions options, bool isDevelopment)`)
  - `ExamPrism.Infrastructure.MultiTenancy.SubdomainMultiTenantStrategy(IOptions<MultiTenancyOptions> options, IHostEnvironment environment) : IMultiTenantStrategy`
  - `ExamPrism.Infrastructure.MultiTenancy.ITenantCacheInvalidator` (`ValueTask InvalidateTenantAsync(Guid tenantId, string slug, CancellationToken ct = default)`)
  - `ExamPrism.Infrastructure.MultiTenancy.CatalogMultiTenantStore(CatalogDbContext db, HybridCache hybridCache, IConnectionStringProtector protector, IOptions<MultiTenancyOptions> options) : IMultiTenantStore<ExamPrismTenantInfo>, ITenantCacheInvalidator`

- [ ] **Step 1: Write failing unit tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/SubdomainAndCatalogStoreTests.cs`**

Create `SubdomainAndCatalogStoreTests` (`namespace Tests.Infrastructure`) covering Spec §5.1–§5.3 and Review Focus #1, #2, #4:
- `SubdomainParser_ClassifiesRootReservedValidInvalidAndOverlappingBaseDomains`: configures `BaseDomains = ["examprism.com", "staging.examprism.com", "localhost"]`, `ReservedSubdomains = ["www", "admin", "api"]`, asserts:
  - `"examprism.com"`, `"localhost"`, `"127.0.0.1"`, `"[::1]"`, `"admin.examprism.com"`, `"WWW.Staging.ExamPrism.com"` -> `Platform` (`Identifier == null`)
  - `"SCHOOL-A.Staging.ExamPrism.com.:8443"` (Host `"SCHOOL-A.Staging.ExamPrism.com."`, Port `8443`) -> `TenantCandidate("school-a")`
  - `"tenant-1.localhost"` -> `TenantCandidate("tenant-1")`
  - `"a.b.examprism.com"`, `"-bad.examprism.com"` -> `InvalidTenantHost`
- `SubdomainParser_XTenantHeaderFallback_WorksOnlyInDevelopmentOnRootHost_AndNeverOverridesExplicitSubdomain`:
  - `Host = "localhost"`, `X-Tenant = "School-A"`, `isDevelopment = true` -> `TenantCandidate("school-a")`
  - `Host = "localhost"`, `X-Tenant = "a.b"`, `isDevelopment = true` -> `InvalidTenantHost`
  - `Host = "tenant-a.localhost"`, `X-Tenant = "tenant-b"`, `isDevelopment = true` -> `TenantCandidate("tenant-a")` (Review Focus #4)
  - `Host = "localhost"`, `X-Tenant = "school-a"`, `isDevelopment = false` -> `Platform` (Review Focus #4)
- `CatalogMultiTenantStore_CachesCiphertextOnlyInDistributedCache_DecryptsOnReadForActiveTenant_AndWithholdsConnectionStringWhenNonActive`: wires `HybridCache` over a recording in-memory `IDistributedCache` + Testcontainers/SQLite/EF InMemory or Postgres `CatalogDbContext` (use `Testcontainers.PostgreSql` `IClassFixture` or per-class container), inserts an `Active` tenant with `protector.Protect("Host=pg;Database=t1;Password=SuperSecretPgPass!")` and a `Suspended` tenant, resolves both via `GetByIdentifierAsync`:
  - `Active` tenant returns `ExamPrismTenantInfo` with decrypted `ConnectionString` containing `"SuperSecretPgPass!"`
  - Raw UTF-8 bytes stored inside `IDistributedCache` do **not** contain `"SuperSecretPgPass!"` (Review Focus #2)
  - `Suspended` tenant returns `ExamPrismTenantInfo` with `Status == TenantStatus.Suspended` and `ConnectionString == null`
- `CatalogMultiTenantStore_NegativeCachesUnknownSlug_AndInvalidatesOnAddOrUpdate`: queries `"missing-org"` twice, asserts `CatalogDbContext` was queried once while cached, then calls `AddAsync` (or `InvalidateTenantAsync`) for `"missing-org"` and verifies `GetByIdentifierAsync("missing-org")` immediately returns the newly added tenant.

- [ ] **Step 2: Run `SubdomainAndCatalogStoreTests` to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~SubdomainAndCatalogStoreTests"`
Expected: FAIL (types do not exist).

- [ ] **Step 3: Implement `SubdomainParser`, `SubdomainMultiTenantStrategy`, `ITenantCacheInvalidator`, and `CatalogMultiTenantStore`**

- `SubdomainParser`:
  - Cache result in `httpContext.Items[HttpContextItemKey]` if already computed on the request.
  - Normalize `rawHost = httpContext.Request.Host.Host.TrimEnd('.').ToLowerInvariant()`.
  - If empty or `IPAddress.TryParse(rawHost.Trim('[', ']'), out _)`: treat as root `Platform` (check dev `X-Tenant` fallback).
  - Order `options.BaseDomains` (normalized lowercase, trimmed of trailing `.`) by `Length` descending.
  - Exact match with `baseDomain`: root `Platform`; if `isDevelopment && options.AllowHeaderFallbackInDevelopment` and header `options.HeaderName` is non-whitespace, normalize header value -> `TenantSlugValidator.IsValid(h) ? TenantCandidate(h) : InvalidTenantHost`.
  - Suffix match `".{baseDomain}"`: extract `prefix`; if `ReservedSubdomains` contains `prefix` (case-insensitive) -> `Platform`; else if `TenantSlugValidator.IsValid(prefix)` -> `TenantCandidate(prefix)`; else -> `InvalidTenantHost`.
- `SubdomainMultiTenantStrategy`: `GetIdentifierAsync(object context)` expects `HttpContext`, calls `SubdomainParser.Parse`, returns `result.Kind == HostResolutionKind.TenantCandidate ? result.Identifier : null`.
- `CatalogMultiTenantStore`:
  - Uses `HybridCache` keys `"catalog:tenant:slug:{slug}"` and `"catalog:tenant:id:{id:D}"` with `CatalogTenantCacheEntry(bool Exists, Guid Id, string Slug, string Name, TenantStatus Status, string? EncryptedConnectionString)`.
  - For missing rows, pass `HybridCacheEntryOptions { Expiration = TimeSpan.FromSeconds(options.NotFoundCacheExpirationSeconds), LocalCacheExpiration = TimeSpan.FromSeconds(options.NotFoundCacheExpirationSeconds) }` and tag `$"catalog:tenant:slug:{slug}"`.
  - For existing rows, pass `Expiration = TimeSpan.FromMinutes(options.CatalogCacheExpirationMinutes)`, `LocalCacheExpiration = TimeSpan.FromMinutes(options.CatalogLocalCacheExpirationMinutes)`, tags `["catalog:tenants", $"catalog:tenant:{row.Id:D}", $"catalog:tenant:slug:{row.Slug}"]`.
  - Decrypt `EncryptedConnectionString` via `protector.Unprotect(...)` **only** when `entry.Status == TenantStatus.Active && !string.IsNullOrWhiteSpace(entry.EncryptedConnectionString)`.
  - Implement `GetByIdentifierAsync`, `GetAsync`, `GetAllAsync`, `AddAsync`, `UpdateAsync`, `RemoveAsync`, and `InvalidateTenantAsync` (calling `hybridCache.RemoveAsync` on slug & id keys plus `RemoveByTagAsync` on slug & id tags).

- [ ] **Step 4: Run `SubdomainAndCatalogStoreTests` to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~SubdomainAndCatalogStoreTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/MultiTenancy/SubdomainParser.cs apps/backend/src/Infrastructure/MultiTenancy/SubdomainMultiTenantStrategy.cs apps/backend/src/Infrastructure/MultiTenancy/ITenantCacheInvalidator.cs apps/backend/src/Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/SubdomainAndCatalogStoreTests.cs
git commit -m "feat(multitenancy): add SubdomainParser, Finbuckle strategy, and ciphertext-cached CatalogMultiTenantStore"
```

---

### Task 4: HTTP Boundary Guards (`TenantAccessGuardMiddleware`, `.RequireTenant()`, `.RequirePlatform()`) & Localization

**Files:**
- Modify: `apps/backend/src/Common/Localization/Resources/SharedResources.resx:73-76`
- Modify: `apps/backend/src/Common/Localization/Resources/SharedResources.vi.resx:73-76`
- Create: `apps/backend/src/Common/Endpoints/TenantProblemResults.cs`
- Create: `apps/backend/src/Common/Endpoints/TenantEndpointExtensions.cs`
- Create: `apps/backend/src/Infrastructure/MultiTenancy/TenantAccessGuardMiddleware.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/TenantGuardAndBoundaryTests.cs`

**Interfaces:**
- Consumes: `SubdomainParser`, `MultiTenancyOptions`, `ExamPrismTenantInfo`, `ITenantContext`, `SharedResources`, `IMultiTenantContextAccessor<ExamPrismTenantInfo>`, `IMultiTenantContextSetter`
- Produces:
  - `Common.Endpoints.TenantBoundaryMode` (`TenantRequired`, `PlatformOnly`) & `TenantBoundaryMetadata(TenantBoundaryMode Mode)`
  - `Common.Endpoints.TenantEndpointExtensions` (`TBuilder RequireTenant<TBuilder>(this TBuilder builder)`, `TBuilder RequirePlatform<TBuilder>(this TBuilder builder)` where `TBuilder : IEndpointConventionBuilder`)
  - `Common.Endpoints.TenantProblemResults` (builds & writes localized RFC 7807 `ProblemDetails` with `traceId` and `errorCode`)
  - `ExamPrism.Infrastructure.MultiTenancy.TenantAccessGuardMiddleware(RequestDelegate next, IOptions<MultiTenancyOptions> options, IHostEnvironment environment)`

- [ ] **Step 1: Write failing guard & boundary tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/TenantGuardAndBoundaryTests.cs`**

Create `TenantGuardAndBoundaryTests` (`namespace Tests.Infrastructure`) using an in-memory `TestServer` (`WebApplicationBuilder` with `UseRequestLocalization`, `UseMultiTenant`, `TenantAccessGuardMiddleware`, and test routes `/tenant-only` `.RequireTenant()`, `/platform-only` `.RequirePlatform()`, `/neutral`) and an in-memory `IMultiTenantStore<ExamPrismTenantInfo>`:
- `TenantAccessGuard_RejectsUnknownInvalidProvisioningSuspendedAndFailedTenants_WithLocalizedProblemDetails_AndClearsMultiTenantContext`:
  - `unknown.examprism.com` -> `404`, `errorCode == "TENANT_NOT_FOUND"`, checks both `vi-VN` (`"Không tìm thấy tổ chức theo tên miền này."`) and `en-US` (`"The requested organization was not found."`)
  - `a.b.examprism.com` -> `404`, `errorCode == "TENANT_NOT_FOUND"`
  - `provisioning.examprism.com` -> `503`, `Retry-After == "5"`, `errorCode == "TENANT_PROVISIONING"`, and verifies `IMultiTenantContextAccessor<ExamPrismTenantInfo>().MultiTenantContext.TenantInfo` was cleared to `null` (Review Focus #3)
  - `suspended.examprism.com` -> `403`, `errorCode == "TENANT_SUSPENDED"`, verifies `TenantInfo` cleared to `null` (Review Focus #3)
  - `failed.examprism.com` -> `403`, `errorCode == "TENANT_UNAVAILABLE"`, verifies `TenantInfo` cleared to `null` (Review Focus #3)
- `EndpointBoundaries_EnforceRequireTenantAndRequirePlatform_WhileAllowingNeutralRoutes`:
  - `examprism.com/tenant-only` -> `400`, `errorCode == "TENANT_REQUIRED"`
  - `examprism.com/platform-only` -> `200`
  - `active.examprism.com/tenant-only` -> `200`
  - `active.examprism.com/platform-only` -> `404`, `errorCode == "PLATFORM_ONLY"`
  - `/neutral` -> `200` on both `examprism.com` and `active.examprism.com`.

- [ ] **Step 2: Run `TenantGuardAndBoundaryTests` to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TenantGuardAndBoundaryTests"`
Expected: FAIL.

- [ ] **Step 3: Add localization keys and implement `TenantProblemResults`, `TenantEndpointExtensions`, and `TenantAccessGuardMiddleware`**

- Add the 7 resource entries from Spec §7.2 (`TenantNotFound`, `TenantProvisioning`, `TenantSuspended`, `TenantUnavailable`, `TenantRequired`, `PlatformOnly`, `TenantTokenMismatch`) to `SharedResources.resx` and `SharedResources.vi.resx` with exact copy from Spec §7.2.
- Implement `TenantProblemResults` producing `ProblemDetails` (`application/problem+json`, `Extensions["traceId"]`, `Extensions["errorCode"]`).
- Implement `TenantAccessGuardMiddleware` per Spec §5.4: for `Provisioning`, `Suspended`, or `Failed`, reset `setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>((ExamPrismTenantInfo?)null)` before writing the response (setting `Retry-After` on `503`).
- Implement `.RequireTenant()` and `.RequirePlatform()` in `TenantEndpointExtensions` via `AddEndpointFilter` checking `httpContext.RequestServices.GetRequiredService<ITenantContext>().HasTenant` (attaching `.RequireTenant()` onto live `/api/auth/*` endpoints happens in Task 6 alongside pipeline wiring so intermediate commits keep all existing integration tests green).

- [ ] **Step 4: Run `TenantGuardAndBoundaryTests` and `ArchitectureTests` to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TenantGuardAndBoundaryTests|FullyQualifiedName~ArchitectureTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Localization/Resources apps/backend/src/Common/Endpoints/TenantProblemResults.cs apps/backend/src/Common/Endpoints/TenantEndpointExtensions.cs apps/backend/src/Infrastructure/MultiTenancy/TenantAccessGuardMiddleware.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/TenantGuardAndBoundaryTests.cs
git commit -m "feat(multitenancy): add TenantAccessGuardMiddleware, endpoint tenancy boundaries, and vi/en localization"
```

---

### Task 5: Cross-Cutting Tenant Isolation (`JwtTenantValidator`, `HybridCacheService`, `RateLimitingSetup`, `TenantWolverineMiddleware`)

**Files:**
- Create: `apps/backend/src/Infrastructure/Identity/JwtTenantValidator.cs`
- Modify: `apps/backend/src/Endpoints/Auth/AuthCookieHelper.cs:20-56`
- Modify: `apps/backend/src/Infrastructure/Caching/HybridCacheService.cs:7-96`
- Modify: `apps/backend/src/Infrastructure/RateLimiting/RateLimitingSetup.cs:75-149`
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs:67-79` (register baseline `services.AddMultiTenant<ExamPrismTenantInfo>()` so `IMultiTenantContextAccessor<ExamPrismTenantInfo>` resolves in existing DI)
- Create: `apps/backend/src/Infrastructure/MultiTenancy/TenantWolverineMiddleware.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/HybridCacheServiceTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/RedisIntegrationTests.cs:84-86,118-120`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CrossCuttingTenantIsolationUnitTests.cs`

**Interfaces:**
- Consumes: `CustomClaimTypes.TenantId`, `TenantProblemResults`, `IMultiTenantContextAccessor<ExamPrismTenantInfo>`, `IMultiTenantContextSetter`, `IMultiTenantStore<ExamPrismTenantInfo>`, `Wolverine.Envelope`
- Produces:
  - `ExamPrism.Infrastructure.Identity.JwtTenantValidator` (`Task ValidateAsync(TokenValidatedContext context)`, `Task HandleChallengeAsync(JwtBearerChallengeContext context)`)
  - `HybridCacheService(HybridCache hybridCache, IOptions<CacheOptions> options, IMultiTenantContextAccessor<ExamPrismTenantInfo> tenantAccessor)` scoping keys/tags to `"t:{tenantId}:"` (plus umbrella tag `"t:{tenantId}"`) or `"platform:"`
  - Tenant-scoped `RateLimitingSetup` partition keys (`"{t:{tenantId}|platform}:auth:ip:{ip}"`, `"{t:{tenantId}|platform}:global:..."`)
  - `ExamPrism.Infrastructure.MultiTenancy.TenantWolverineMiddleware` (`BeforeAsync` + `Finally` restoring prior `IMultiTenantContext<ExamPrismTenantInfo>`)

- [ ] **Step 1: Write failing unit tests in `HybridCacheServiceTests.cs` and `CrossCuttingTenantIsolationUnitTests.cs`**

- Update `HybridCacheServiceTests` DI setup to include `services.AddMultiTenant<ExamPrismTenantInfo>()`.
- Create `CrossCuttingTenantIsolationUnitTests` (`namespace Tests.Infrastructure`) testing:
  - `JwtTenantValidator_AllowsMatchingTenantClaim_RejectsCrossTenantOrMissingClaimOnTenantHost_AndRejectsTenantTokenOnPlatformHost`: verifies `context.Result.Failure` is null when `"tenant_id"` matches active `ExamPrismTenantInfo.Id`, fails with `"TENANT_TOKEN_MISMATCH"` marker when `"tenant_id"` belongs to another tenant or is absent on a tenant host, and fails with `"TENANT_TOKEN_MISMATCH"` when a token carrying `"tenant_id"` is presented on a Platform host (`TenantInfo == null`).
  - `HybridCacheService_IsolatesIdenticalKeysAndTagsAcrossTenantsAndPlatform_AndSupportsUmbrellaTenantEviction`: sets `"quiz:1"` with tag `"quizzes"` under Tenant A (`"val-A"`), Tenant B (`"val-B"`), and Platform (`"val-P"`); verifies each context reads its own value, `RemoveByTagAsync("quizzes")` under Tenant A evicts only Tenant A's entry, and `hybridCache.RemoveByTagAsync($"t:{tenantBId:D}")` evicts all of Tenant B's entries without touching Platform.
  - `TenantWolverineMiddleware_SetsTenantContextForBackgroundEnvelopeByIdOrSlug_RestoresPriorContextInFinally_AndThrowsForInactiveOrUnknownTenant`: exercises `BeforeAsync` + `Finally` for a background `Envelope { TenantId = "tenant-a" }` when `accessor.MultiTenantContext.TenantInfo` is initially `null`, asserts `accessor.MultiTenantContext.TenantInfo.Identifier == "tenant-a"` during handler execution and is restored to `null` after `Finally` (Review Focus #5); also asserts `Suspended` or unknown `Envelope.TenantId` throws `InvalidOperationException`.

- [ ] **Step 2: Run unit tests to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~HybridCacheServiceTests|FullyQualifiedName~CrossCuttingTenantIsolationUnitTests"`
Expected: FAIL.

- [ ] **Step 3: Implement `JwtTenantValidator`, `AuthCookieHelper` explicit `Domain = null`, tenant-scoped `HybridCacheService` & `RateLimitingSetup`, and `TenantWolverineMiddleware`**

- `JwtTenantValidator`: store `"ExamPrism.AuthFailureCode" = "TENANT_TOKEN_MISMATCH"` in `context.HttpContext.Items` on mismatch and call `context.Fail(...)`; in `HandleChallengeAsync`, when `Items` contains `"TENANT_TOKEN_MISMATCH"`, call `context.HandleResponse()` and write `401` localized `ProblemDetails` (`TenantTokenMismatch`, `errorCode = "TENANT_TOKEN_MISMATCH"`).
- `AuthCookieHelper`: explicitly set `Domain = null` on all `CookieOptions` instances in `SetAuthCookies` and `ClearAuthCookies`.
- `HybridCacheService`: compute `scopePrefix = tenantInfo is { Status: TenantStatus.Active } ? $"t:{tenantInfo.Id}:" : "platform:"`; transform `key` -> `scopePrefix + key` and `tags` -> `tags.Select(t => scopePrefix + t)` (appending `$"t:{tenantInfo.Id}"` when in active tenant context). Register `services.AddMultiTenant<ExamPrismTenantInfo>()` in `DependencyInjection.cs` so `IMultiTenantContextAccessor<ExamPrismTenantInfo>` resolves cleanly, and update `RedisIntegrationTests.cs` key checks (`$"examprism:platform:{cacheKey}"` and `"examprism:ratelimit:*:auth:*"`).
- `RateLimitingSetup`: resolve `var tenantInfo = httpContext.GetMultiTenantContext<ExamPrismTenantInfo>()?.TenantInfo; var scope = tenantInfo is { Status: TenantStatus.Active } ? $"t:{tenantInfo.Id}" : "platform";` and prefix partition keys (`$"{scope}:auth:ip:{ip}"`, `$"{scope}:global:user:{userId}"`, `$"{scope}:global:ip:{ip}"`).
- `TenantWolverineMiddleware`: instance Wolverine middleware holding `_priorContext` + `_overridden` flag; `BeforeAsync` populates `setter.MultiTenantContext` when `envelope.TenantId` is non-empty (and not `"*DEFAULT*"`) and current context does not already match; `Finally` restores `setter.MultiTenantContext = _priorContext` whenever `_overridden` is `true`.

- [ ] **Step 4: Run unit tests to verify pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~HybridCacheServiceTests|FullyQualifiedName~CrossCuttingTenantIsolationUnitTests|FullyQualifiedName~AuthCookieHelperTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/Identity/JwtTenantValidator.cs apps/backend/src/Endpoints/Auth/AuthCookieHelper.cs apps/backend/src/Infrastructure/Caching/HybridCacheService.cs apps/backend/src/Infrastructure/RateLimiting/RateLimitingSetup.cs apps/backend/src/Infrastructure/DependencyInjection.cs apps/backend/src/Infrastructure/MultiTenancy/TenantWolverineMiddleware.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/HybridCacheServiceTests.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/RedisIntegrationTests.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/CrossCuttingTenantIsolationUnitTests.cs
git commit -m "feat(multitenancy): isolate JWT validator, HybridCache keys/tags, rate-limit partitions, and Wolverine background envelopes"
```

---

### Task 6: Wire `TokenService`, Auth Endpoints, `DependencyInjection` & `Program.cs`, Upgrade Multi-DB Testcontainers, and Add `MultiTenancyIntegrationTests`

**Files:**
- Modify: `apps/backend/src/appsettings.json`
- Modify: `apps/backend/src/appsettings.Development.json`
- Modify: `apps/backend/src/Infrastructure/Identity/TokenService.cs:10-38`
- Modify: `apps/backend/src/Endpoints/Auth/RegisterEndpoint.cs:40-46`
- Modify: `apps/backend/src/Endpoints/Auth/LoginEndpoint.cs:40-44`
- Modify: `apps/backend/src/Endpoints/Auth/RefreshEndpoint.cs:35-45`
- Modify: `apps/backend/src/Endpoints/Auth/LogoutEndpoint.cs:25-35`
- Modify: `apps/backend/src/Endpoints/Auth/GetMeEndpoint.cs:25-35`
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs`
- Modify: `apps/backend/src/Program.cs`
- Modify: `apps/backend/src/Endpoints/Test/TestValidationEndpoints.cs` (add dev-only `/test/platform-only` `.RequirePlatform()` probe route for boundary integration testing)
- Modify: `apps/backend/tests/ExamPrism.Tests/Identity/TokenServiceTests.cs:23-57`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DatabaseIntegrationTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Features/AuthEndpointTests.cs:71-76,164-169,268-276`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/RedisIntegrationTests.cs:31-48`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/MultiTenancyIntegrationTests.cs`

**Interfaces:**
- Consumes: All components from Tasks 1–5
- Produces:
  - `TokenService(IOptions<JwtOptions> options, TimeProvider timeProvider, ITenantContext tenantContext)` emitting `Claim(CustomClaimTypes.TenantId, tenantId.ToString("D"))`
  - `.RequireTenant()` enforced on all 5 `/api/auth/*` endpoints
  - Fully wired multi-tenant `ExamPrism` runtime (`AddInfrastructure` + `Program.cs`)
  - Multi-DB `CustomWebApplicationFactory` (`examprism_catalog_test`, `examprism_tenant_a`, `examprism_tenant_b`, seeded lifecycle tenants, `ClientOptions.BaseAddress = new Uri("http://tenant-a.localhost")`, `CreateTenantClient(string slug)`, `CreatePlatformClient()`, `CreateTenantScopeAsync(string slug = "tenant-a")`)
  - End-to-end `MultiTenancyIntegrationTests`

- [ ] **Step 1: Write failing `TokenServiceTests`, `DependencyInjectionTests`, and `MultiTenancyIntegrationTests`**

- Update `TokenServiceTests`:
  - Pass mocked `ITenantContext` (`HasTenant = true`, `TenantId = expectedTenantId`), assert generated JWT contains claim `"tenant_id" == expectedTenantId.ToString("D")`.
  - Add `GenerateAccessToken_WhenNoActiveTenant_ThrowsInvalidOperationException`.
- Update `DependencyInjectionTests`:
  - Switch fixture key to `"ConnectionStrings:CatalogConnection"`; assert missing `CatalogConnection` throws `InvalidOperationException("*CatalogConnection*")`.
  - Assert `CatalogDbContext`, `AppDbContext`, `ITenantContext`, `IConnectionStringProtector`, `ITenantConnectionStringNormalizer`, `ITenantCacheInvalidator`, `IMultiTenantStore<ExamPrismTenantInfo>`, and `"catalog_db"` health check are registered.
  - Add `ResolveAppDbContext_WithoutActiveTenantContext_ThrowsInvalidOperationException`: creates scope from `BuildServiceProvider()`, asserts `scope.ServiceProvider.GetRequiredService<AppDbContext>()` throws `InvalidOperationException`.
- Create `MultiTenancyIntegrationTests : IClassFixture<CustomWebApplicationFactory>` (`namespace Tests.Infrastructure`) testing Spec §8.2:
  - `PhysicalDatabaseIsolation_SameEmailRegistersIndependentlyAcrossTenantAAndTenantB_WithSeparateCredentials`: registers `alice@school.edu` with `"PasswordTenantA1!"` on `tenant-a.localhost` and `"PasswordTenantB2!"` on `tenant-b.localhost` -> both return `201 Created`; logging into `tenant-a` with `"PasswordTenantB2!"` returns `401`, and with `"PasswordTenantA1!"` returns `200`.
  - `CrossTenantJwtRejection_BearerAndCookieFromTenantA_Return401TenantTokenMismatchOnTenantBAndPlatform`: uses `tenant-a` access token against `tenant-b.localhost/api/auth/me` (both `Authorization: Bearer` and `Cookie: access_token=...`) -> `401` with `errorCode == "TENANT_TOKEN_MISMATCH"`.
  - `TenantLifecycleGuards_Return404_503WithRetryAfter_And403_LocalizedInViAndEn`: hits `unknown.localhost`, `provisioning-org.localhost`, `suspended-org.localhost`, `failed-org.localhost` with `Accept-Language: vi-VN` and `en-US`, asserting exact statuses, `Retry-After: 5`, `errorCode`s, and localized `detail` strings from Spec §7.2.
  - `DomainBoundaries_PlatformRootRejectsTenantAuth400_ServesHealth200_AndTenantHostRejectsPlatformOnly404`: `CreatePlatformClient()` `POST /api/auth/login` -> `400 TENANT_REQUIRED`; `GET /health` -> `200 Healthy`; `CreateTenantClient("tenant-a")` `GET /test/platform-only` -> `404 PLATFORM_ONLY`.
  - `RateLimiting_ExhaustingAuthQuotaOnTenantA_DoesNotThrottleTenantB`: uses `WithWebHostBuilder` (`RateLimiting:Auth:PermitLimit = 2`), sends 3 failed logins to `tenant-a.localhost` (3rd returns `429`), then sends login to `tenant-b.localhost` -> returns `401` (not `429`).
  - `WolverineBackgroundBridge_InvokeForTenantAsync_ExecutesHandlerAgainstTargetTenantDatabaseWithoutHttpContext`: registers a user on `tenant-b.localhost`, then from `_factory.Services` (no `HttpContext`) invokes `bus.InvokeForTenantAsync<Result<LoginResult>>("tenant-b", new LoginCommand(email, password))` -> succeeds against `examprism_tenant_b` while `"tenant-a"` returns `401 INVALID_CREDENTIALS`.

- [ ] **Step 2: Run `TokenServiceTests`, `DependencyInjectionTests`, and `MultiTenancyIntegrationTests` to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~TokenServiceTests|FullyQualifiedName~DependencyInjectionTests|FullyQualifiedName~MultiTenancyIntegrationTests"`
Expected: FAIL.

- [ ] **Step 3: Wire `TokenService`, Auth endpoints, `appsettings*.json`, `DependencyInjection.cs`, `Program.cs`, `/test/platform-only`, and multi-DB test fixtures**

- Update `TokenService`: inject `ITenantContext`, require `tenantContext.HasTenant && tenantContext.TenantId.HasValue` (else throw `InvalidOperationException("Active tenant context is required to generate a tenant access token.")`), and emit `new Claim(CustomClaimTypes.TenantId, tenantContext.TenantId.Value.ToString("D"))`.
- Chain `.RequireTenant()` onto `RegisterEndpoint`, `LoginEndpoint`, `RefreshEndpoint`, `LogoutEndpoint`, and `GetMeEndpoint`.
- Update `appsettings.json` and `appsettings.Development.json` with `ConnectionStrings:CatalogConnection` and `MultiTenancy` section per Spec §2.2.
- Update `DependencyInjection.cs`:
  - Require `configuration.GetConnectionString("CatalogConnection")`.
  - Register `CatalogDbContext` (`UseNpgsql(catalogConnection)`), `AddDataProtection()`, `MultiTenancyOptions`, `IConnectionStringProtector`, `ITenantConnectionStringNormalizer`, `ITenantContext`, `CatalogMultiTenantStore` (`ITenantCacheInvalidator`), and Finbuckle:
    ```csharp
    services.AddMultiTenant<ExamPrismTenantInfo>()
        .WithStrategy<SubdomainMultiTenantStrategy>(ServiceLifetime.Scoped)
        .WithStore<CatalogMultiTenantStore>(ServiceLifetime.Scoped);
    ```
  - Register fail-fast `AppDbContext` per Spec §4.4.
  - Register `.AddDbContextCheck<CatalogDbContext>(name: "catalog_db")`.
  - Wire `JwtTenantValidator.ValidateAsync` into `JwtBearerEvents.OnTokenValidated` and `JwtTenantValidator.HandleChallengeAsync` into `JwtBearerEvents.OnChallenge`.
- Update `Program.cs`:
  - Add `opts.Policies.AddMiddleware<TenantWolverineMiddleware>();` inside `UseWolverine`.
  - Order middleware per Spec §7.1 (`UseRequestLocalization` -> `UseExceptionHandler` -> `UseForwardedHeaders` -> `UseMultiTenant` -> `UseMiddleware<TenantAccessGuardMiddleware>` -> `UseAuthentication` -> `UseRateLimiter` -> `UseAuthorization` -> `MapOpenApi`/`MapScalarApiReference` in Development -> `MapEndpoints`).
- Add `app.MapGet("/test/platform-only", () => Results.Ok(new { Scope = "platform" })).RequirePlatform();` inside `TestValidationEndpoints.cs` (auto-excluded outside Development by `EndpointExtensions`).
- Upgrade `CustomWebApplicationFactory` (and `RedisWebApplicationFactory` in `RedisIntegrationTests.cs`):
  - Set `ClientOptions.BaseAddress = new Uri("http://tenant-a.localhost")` in constructor; configure `ConnectionStrings:CatalogConnection` and `MultiTenancy:BaseDomains:0 = "localhost"`.
  - In `InitializeAsync()`, create physical databases (`examprism_tenant_a`, `examprism_tenant_b`) on the container via `NpgsqlConnection` (`CREATE DATABASE ...`), run `CatalogDbContext.Database.MigrateAsync()`, seed `tenant-a` (`Active`), `tenant-b` (`Active`), `provisioning-org` (`Provisioning`), `suspended-org` (`Suspended`), `failed-org` (`Failed`) using `IConnectionStringProtector.Protect(...)`, and run `AppDbContext.Database.MigrateAsync()` inside tenant-initialized scopes for `tenant-a` and `tenant-b`.
  - Expose `CreateTenantClient(string slug)`, `CreatePlatformClient()`, and `CreateTenantScopeAsync(string slug = "tenant-a")`.
- Update `DatabaseIntegrationTests.cs` and `AuthEndpointTests.cs` (use `await _factory.CreateTenantScopeAsync()` for direct `AppDbContext` assertions).

- [ ] **Step 4: Run the entire backend test suite to verify 100% green**

Run: `dotnet test apps/backend/ExamPrism.slnx`
Expected: PASS (0 failed tests across Architecture, Unit, Multi-Tenancy, Auth, Rate Limiting, HybridCache, and Redis Testcontainers suites).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/appsettings.json apps/backend/src/appsettings.Development.json apps/backend/src/Infrastructure/Identity/TokenService.cs apps/backend/src/Endpoints/Auth apps/backend/src/Infrastructure/DependencyInjection.cs apps/backend/src/Program.cs apps/backend/src/Endpoints/Test/TestValidationEndpoints.cs apps/backend/tests/ExamPrism.Tests
git commit -m "feat(multitenancy): wire Finbuckle multi-tenant pipeline and multi-DB Testcontainers integration suite"
```
