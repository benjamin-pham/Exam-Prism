# Design Specification: Multi-Tenant Core Foundation (Subdomain + Database-per-Tenant with Finbuckle v10)

**Date:** 2026-11-02  
**Status:** Draft (Pending User Spec Review)  
**Scope:** Architectural Subsystem — Sub-project 1 of 3  

---

## 1. Context, Goals, and Multi-Project Roadmap

ExamPrism backend currently operates as a single-tenant ASP.NET Core 10 Minimal API (`IEndpoint` auto-registration, Wolverine `IHandler` vertical slices, PostgreSQL + EF Core `AppDbContext`, ASP.NET Core Identity + JWT/Refresh Tokens, `HybridCacheService`, and fixed-window rate limiting).

We are evolving ExamPrism into a **Self-Service Multi-Tenant SaaS** capable of supporting **>500 tenants**, where:
* Each tenant operates on an isolated **subdomain** (e.g., `school-a.examprism.com`).
* Each tenant's business data and organization users (`Users`, `Roles`, `RefreshTokens`) live in an isolated **physical PostgreSQL database** (Database-per-Tenant).
* A central **Master/Catalog PostgreSQL database** (`CatalogDbContext`) manages tenant lifecycle metadata and platform administration.

To keep specifications, implementation plans, and code reviews modular and verifiable, the full multi-tenant initiative is decomposed into **three sequential sub-projects**:

1. **Sub-project 1 — Multi-Tenant Core Foundation (This Specification)**:
   * `Finbuckle.MultiTenant` v10 integration (`SubdomainMultiTenantStrategy`, `ExamPrismTenantInfo`, `CatalogMultiTenantStore` backed by `HybridCache`).
   * `CatalogDbContext` (`Tenants` table, DataProtection connection-string encryption, separate Catalog vs. Tenant EF Core migration pipelines).
   * Dynamic per-tenant `AppDbContext` with fail-fast tenant enforcement and Npgsql connection pool tuning for >500 databases.
   * Cross-cutting tenant isolation across `ITenantContext`, JWT (`tenant_id` claim) + Host-Only Cookies, `HybridCacheService`, `RateLimitingSetup`, `/health`, and a `TenantWolverineMiddleware` bridge for background Wolverine messages.
2. **Sub-project 2 — Platform Identity & Admin Tenant Management (Next Spec)**:
   * Full ASP.NET Core Identity in `CatalogDbContext` (`PlatformUser`, `PlatformRole`, `PlatformRefreshToken`), initial SuperAdmin seeding, Platform Auth endpoints on root/admin domains, and Admin Tenant Management APIs (list, inspect, suspend/reactivate with instant cache invalidation).
3. **Sub-project 3 — Async Self-Service Provisioning & Fleet Migration Pipeline (Third Spec)**:
   * Public self-service onboarding on root domain (`POST /api/tenants` returning `202 Accepted` with `Provisioning` status, slug availability check, `GET /api/tenants/{slug}/status` polling), background Wolverine provisioning worker (`CREATE DATABASE`, EF Core `MigrateAsync`, initial Tenant Owner seeding, retry/compensation), and rolling schema migration runner for >500 existing tenant databases.

### 1.1. Non-Goals of Sub-project 1
* Platform Admin Identity tables and Admin CRUD endpoints (deferred to Sub-project 2).
* Public `POST /api/tenants` self-service provisioning worker and fleet migration CLI/worker (deferred to Sub-project 3).

---

## 2. Packages & Configuration Schema

### 2.1. NuGet Packages (`Directory.Packages.props` & `ExamPrism.csproj`)
Add official .NET 10 Finbuckle packages:
* `Finbuckle.MultiTenant` (`10.1.5`)
* `Finbuckle.MultiTenant.AspNetCore` (`10.1.5`)

> **Architectural Decision:** Do **not** install `Finbuckle.MultiTenant.EntityFrameworkCore`. Because ExamPrism uses physical **Database-per-Tenant** isolation, `AppDbContext` tables must not carry redundant `TenantId` shadow columns or shared-table global `WHERE TenantId = ...` query filters.

### 2.2. Configuration Schema (`appsettings.json`)

```json
{
  "ConnectionStrings": {
    "CatalogConnection": "Host=localhost;Port=5432;Database=examprism_catalog_dev;Username=postgres;Password=postgres",
    "Redis": ""
  },
  "MultiTenancy": {
    "BaseDomains": [ "examprism.com", "localhost" ],
    "ReservedSubdomains": [ "www", "admin", "api" ],
    "AllowHeaderFallbackInDevelopment": true,
    "HeaderName": "X-Tenant",
    "CatalogCacheExpirationMinutes": 30,
    "CatalogLocalCacheExpirationMinutes": 5,
    "NotFoundCacheExpirationSeconds": 30,
    "ProvisioningRetryAfterSeconds": 5,
    "DatabasePool": {
      "MaxPoolSize": 15,
      "MinPoolSize": 0,
      "ConnectionIdleLifetimeSeconds": 30,
      "ConnectionPruningIntervalSeconds": 10
    }
  }
}
```

### 2.3. `MultiTenancyOptions` (`ExamPrism.Infrastructure.MultiTenancy`)
* `SectionName = "MultiTenancy"`
* `BaseDomains` (`string[]`, default `["localhost"]`): Recognized root domains (matched case-insensitively, without port).
* `ReservedSubdomains` (`string[]`, default `["www", "admin", "api"]`): Subdomains under `BaseDomains` treated as **Platform Context** rather than tenant identifiers.
* `AllowHeaderFallbackInDevelopment` (`bool`, default `true`): When `IHostEnvironment.IsDevelopment()` is `true` **and** the incoming host is a Platform root host (no subdomain), allows resolving the tenant identifier from `HeaderName` (`X-Tenant`) for Scalar/OpenAPI local testing. Strictly ignored in non-Development environments and never overrides an explicit host subdomain.
* `HeaderName` (`string`, default `"X-Tenant"`).
* `CatalogCacheExpirationMinutes` (`int`, default `30`) & `CatalogLocalCacheExpirationMinutes` (`int`, default `5`): L2/L1 TTLs for cached catalog tenant entries.
* `NotFoundCacheExpirationSeconds` (`int`, default `30`): Short TTL for negative cache sentinel entries to protect `CatalogDbContext` against random subdomain scanning.
* `ProvisioningRetryAfterSeconds` (`int`, default `5`): Value emitted in the HTTP `Retry-After` header when a tenant's status is `Provisioning`.
* `DatabasePool` (`TenantDatabasePoolOptions`):
  * `MaxPoolSize` (`int`, default `15`)
  * `MinPoolSize` (`int`, default `0`)
  * `ConnectionIdleLifetimeSeconds` (`int`, default `30`)
  * `ConnectionPruningIntervalSeconds` (`int`, default `10`)

---

## 3. Domain & Tenant Context Abstractions

### 3.1. `Common.MultiTenancy` (Clean Layer — Safe for `ExamPrism.Features`)
To satisfy `ArchitectureTests` (`ExamPrism.Features` must never reference `Microsoft.AspNetCore.Http`, `Routing`, `Mvc`, or infrastructure libraries):

* **`TenantStatus` enum**:
  ```csharp
  namespace Common.MultiTenancy;

  public enum TenantStatus
  {
      Provisioning = 0,
      Active = 1,
      Suspended = 2,
      Failed = 3
  }
  ```
* **`ITenantContext` interface**:
  ```csharp
  namespace Common.MultiTenancy;

  public interface ITenantContext
  {
      bool HasTenant { get; }
      Guid? TenantId { get; }
      string? Identifier { get; }
      string? Name { get; }
  }
  ```

### 3.2. `ExamPrism.Infrastructure.MultiTenancy`
* **`ExamPrismTenantInfo` record**:
  Inherits from `Finbuckle.MultiTenant.Abstractions.TenantInfo` (`public sealed record ExamPrismTenantInfo : TenantInfo`):
  * `string Id` (canonical lowercase GUID string, `Tenant.Id.ToString("D")`)
  * `string Identifier` (canonical lowercase subdomain slug, e.g., `"school-a"`)
  * `string? Name` (organization display name)
  * `Guid TenantGuid` (parsed `Guid` convenience property)
  * `TenantStatus Status`
  * `string? ConnectionString` (decrypted in memory; `null` while `Status != TenantStatus.Active`)
* **`TenantContext : ITenantContext`** (registered as `Scoped`):
  Reads `IMultiTenantContextAccessor<ExamPrismTenantInfo>.MultiTenantContext.TenantInfo`. Returns `HasTenant = true` **only** when `TenantInfo is { Status: TenantStatus.Active }`.

---

## 4. Persistence Architecture, Encryption & Connection Pooling

### 4.1. `CatalogDbContext` & `Tenant` Entity (`ExamPrism.Infrastructure.Persistence.Catalog`)
* Bound statically to `ConnectionStrings:CatalogConnection` (throwing `InvalidOperationException("Connection string 'CatalogConnection' not found.")` at startup if missing or whitespace).
* **`Tenant` entity**:
  * `Guid Id` (PK)
  * `string Slug` (`varchar(63)`, required, unique index `IX_Tenants_Slug`, validated against DNS label regex `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`)
  * `string Name` (`varchar(200)`, required)
  * `string? EncryptedConnectionString` (`text`, nullable while `Status == Provisioning` or `Failed`)
  * `TenantStatus Status` (stored via `HasConversion<string>().HasMaxLength(32)`, indexed via `IX_Tenants_Status`)
  * `string? FailureReason` (`varchar(1000)`, nullable)
  * `DateTimeOffset CreatedAt` (required)
  * `DateTimeOffset? ProvisionedAt` (nullable)
  * `DateTimeOffset UpdatedAt` (required)

### 4.2. Separate EF Core Migration Pipelines
To keep Master Catalog schema evolution completely independent of Tenant database schema evolution:
* **Catalog Migrations**: Output directory `Infrastructure/Persistence/Migrations/Catalog`, supported by `CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>`.
* **Tenant Migrations**: Output directory `Infrastructure/Persistence/Migrations/Tenant`, supported by `AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>`.
* Generate initial baseline migrations for both `CatalogDbContext` (`InitialCatalog`) and `AppDbContext` (`InitialTenant`) as part of Sub-project 1 so Sub-projects 2 & 3 build cleanly via `Database.MigrateAsync()`.

### 4.3. Connection String Encryption (`IConnectionStringProtector`)
* Registered via `services.AddDataProtection()` and `IConnectionStringProtector` (`DataProtectionConnectionStringProtector`) using purpose string `"ExamPrism.TenantConnectionStrings.v1"`.
* **Defense-in-Depth (DB + Redis L2)**:
  * `Tenant.EncryptedConnectionString` is encrypted before saving to `CatalogDbContext`.
  * `CatalogMultiTenantStore` caches `CatalogTenantCacheEntry` containing the **ciphertext** (`EncryptedConnectionString`), never the plaintext password, so a Redis L2 memory inspection or dump never exposes PostgreSQL credentials.
  * `IConnectionStringProtector.Unprotect(...)` is called in-process in RAM when constructing `ExamPrismTenantInfo` after reading from `HybridCache`.

### 4.4. Npgsql Pool Tuning (>500 Tenant DBs) & Fail-Fast `AppDbContext`
In Npgsql, each distinct database name maintains its own in-process connection pool. To prevent >500 tenant pools from exhausting PostgreSQL `max_connections`:
* **`ITenantConnectionStringNormalizer` (`TenantConnectionStringNormalizer`)**:
  Parses decrypted tenant connection strings with `NpgsqlConnectionStringBuilder` and applies `MultiTenancyOptions.DatabasePool`:
  * `Pooling = true`
  * `MinPoolSize = poolOptions.MinPoolSize` (`0` — idle tenants hold zero PostgreSQL connections)
  * `MaxPoolSize = poolOptions.MaxPoolSize` (`15`)
  * `ConnectionIdleLifetime = poolOptions.ConnectionIdleLifetimeSeconds` (`30`)
  * `ConnectionPruningInterval = poolOptions.ConnectionPruningIntervalSeconds` (`10`)
* **Fail-Fast `AppDbContext` Registration**:
  ```csharp
  services.AddDbContext<AppDbContext>((sp, options) =>
  {
      var tenantInfo = sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>()
          .MultiTenantContext.TenantInfo;

      if (tenantInfo is null || tenantInfo.Status != TenantStatus.Active || string.IsNullOrWhiteSpace(tenantInfo.ConnectionString))
      {
          throw new InvalidOperationException(
              "An active tenant context with a valid connection string is required to resolve AppDbContext.");
      }

      var normalizer = sp.GetRequiredService<ITenantConnectionStringNormalizer>();
      options.UseNpgsql(normalizer.Normalize(tenantInfo.ConnectionString));
  });
  ```
  * Guarantees `AppDbContext` and `UserManager<ApplicationUser>` share the exact same scoped `AppDbContext` instance per request/job.
  * Guarantees accidental resolution of `AppDbContext` in Platform Context fails immediately and **never** falls back to Master DB.

---

## 5. Subdomain Resolution, Cached Store & HTTP Boundary Guards

### 5.1. Host Classification (`SubdomainParser`)
Given `host` (lowercased `HttpContext.Request.Host.Host`, stripped of trailing dot) and configured `BaseDomains` / `ReservedSubdomains`:
1. **Exact match with any `baseDomain`** (e.g., `examprism.com`, `localhost`), OR an IP literal (`127.0.0.1`, `[::1]`):
   * Classified as `HostResolutionKind.Platform`.
   * If `IsDevelopment() && AllowHeaderFallbackInDevelopment` and request header `X-Tenant` has a non-whitespace value `h`:
     * Trimmed, lowercased `h` is classified as `HostResolutionKind.TenantCandidate(slug)` if valid single DNS label, else `HostResolutionKind.InvalidTenantHost`.
2. **Ends with `".{baseDomain}"`** (matching longest `baseDomain` first):
   * Extract prefix before `".{baseDomain}"`.
   * If prefix contains a dot (multi-level subdomain like `a.b.examprism.com`) or fails DNS slug regex `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$`: classified as `HostResolutionKind.InvalidTenantHost`.
   * If prefix matches `ReservedSubdomains` (e.g., `www`, `admin`, `api`): classified as `HostResolutionKind.Platform`.
   * Otherwise: classified as `HostResolutionKind.TenantCandidate(prefix)`.
3. **Unrecognized host** (does not match any `BaseDomains`):
   * Classified as `HostResolutionKind.Platform` (leaving host validation to ASP.NET Core `AllowedHosts`).

Store the parsed `HostResolutionResult` in `HttpContext.Items` so both `SubdomainMultiTenantStrategy` and `TenantAccessGuardMiddleware` reuse the single parse result.

### 5.2. `SubdomainMultiTenantStrategy : IMultiTenantStrategy`
* Returns `slug` when `HostResolutionResult` is `TenantCandidate(slug)`; otherwise returns `null`.

### 5.3. `CatalogMultiTenantStore : IMultiTenantStore<ExamPrismTenantInfo>` & `ITenantCacheInvalidator`
* Uses `HybridCache` directly with dedicated `"catalog:"` keys (bypassing `HybridCacheService` tenant-scoping):
  * Slug lookup key: `"catalog:tenant:slug:{slug}"`
  * ID lookup key: `"catalog:tenant:id:{id}"`
  * Invalidation tags: `["catalog:tenants", $"catalog:tenant:{id}", $"catalog:tenant:slug:{slug}"]`
* **Negative Caching**:
  * Cache payload `CatalogTenantCacheEntry(bool Exists, Guid Id, string Slug, string Name, TenantStatus Status, string? EncryptedConnectionString)`.
  * When `CatalogDbContext.Tenants.AsNoTracking()` returns `null`, stores `CatalogTenantCacheEntry(Exists: false, ...)` with `Expiration = TimeSpan.FromSeconds(options.NotFoundCacheExpirationSeconds)` and tag `$"catalog:tenant:slug:{slug}"`.
  * When `Exists == true`, decrypts `EncryptedConnectionString` (if present and `Status == Active`) via `IConnectionStringProtector.Unprotect` and returns `ExamPrismTenantInfo`.
* **`ITenantCacheInvalidator`**:
  * `InvalidateTenantAsync(Guid tenantId, string slug, CancellationToken ct)` removes `"catalog:tenant:slug:{slug}"`, `"catalog:tenant:id:{id}"`, and calls `RemoveByTagAsync($"catalog:tenant:{id}")` + `RemoveByTagAsync($"catalog:tenant:slug:{slug}")`. Used by `AddAsync` / `UpdateAsync` / `RemoveAsync` and later by Sub-projects 2 & 3.

### 5.4. `TenantAccessGuardMiddleware`
Executes immediately after `app.UseMultiTenant()` and **before** `UseAuthentication()` and `UseRateLimiter()`:
1. If `HostResolutionResult` is `Platform`: continue (`await next(context)`).
2. If `HostResolutionResult` is `InvalidTenantHost`: write localized `404 ProblemDetails` (`errorCode = "TENANT_NOT_FOUND"`).
3. If `HostResolutionResult` is `TenantCandidate(slug)`:
   * Inspect `context.GetMultiTenantContext<ExamPrismTenantInfo>().TenantInfo`:
   * `null` -> write localized `404 ProblemDetails` (`errorCode = "TENANT_NOT_FOUND"`).
   * `Status == TenantStatus.Provisioning` -> reset Finbuckle `IMultiTenantContextSetter` to empty context, set header `Retry-After: {ProvisioningRetryAfterSeconds}`, write localized `503 ProblemDetails` (`errorCode = "TENANT_PROVISIONING"`).
   * `Status == TenantStatus.Suspended` -> reset Finbuckle setter, write localized `403 ProblemDetails` (`errorCode = "TENANT_SUSPENDED"`).
   * `Status == TenantStatus.Failed` -> reset Finbuckle setter, write localized `403 ProblemDetails` (`errorCode = "TENANT_UNAVAILABLE"`).
   * `Status == TenantStatus.Active` -> continue (`await next(context)`).

### 5.5. Endpoint Tenancy Boundaries (`.RequireTenant()` & `.RequirePlatform()`)
Extensions on `TBuilder where TBuilder : IEndpointConventionBuilder` (`Common.Endpoints.TenantEndpointExtensions`):
* **`.RequireTenant()`**: Adds `TenantBoundaryMetadata(TenantBoundaryMode.TenantRequired)` and an endpoint filter that checks `ITenantContext.HasTenant`. If `false`, short-circuits with localized `400 ProblemDetails` (`errorCode = "TENANT_REQUIRED"`).
  * Applied to all 5 existing Auth endpoints (`RegisterEndpoint`, `LoginEndpoint`, `RefreshEndpoint`, `LogoutEndpoint`, `GetMeEndpoint`).
* **`.RequirePlatform()`**: Adds `TenantBoundaryMetadata(TenantBoundaryMode.PlatformOnly)` and an endpoint filter that checks `ITenantContext.HasTenant`. If `true`, short-circuits with localized `404 ProblemDetails` (`errorCode = "PLATFORM_ONLY"`).
* **Neutral Endpoints** (neither metadata): `GetHealthEndpoint` (`/health`), `/openapi/*`, `/scalar/*`, and development-only `ExamPrism.Endpoints.Test.*` run in either Platform or Active Tenant context.

---

## 6. Cross-Cutting Isolation & Wolverine Bridge

### 6.1. JWT Tenant Claim & Host-Only Cookies
* **Claim Name**: `public const string TenantId = "tenant_id";` (defined in `Common.Security.CustomClaimTypes`).
* **`TokenService.GenerateAccessToken(ApplicationUser user)`**:
  * Injects `ITenantContext`. Requires `ITenantContext.TenantId` to be non-null (throws `InvalidOperationException` if called without an active tenant context) and adds `new Claim("tenant_id", tenantId.Value.ToString("D"))`.
* **`JwtBearerEvents.OnTokenValidated`**:
  * Resolves `IMultiTenantContextAccessor<ExamPrismTenantInfo>().MultiTenantContext.TenantInfo`.
  * **Active Tenant Context (`tenantInfo is { Status: TenantStatus.Active }`)**:
    * Extracts `"tenant_id"` claim from `context.Principal`.
    * If missing or `!string.Equals(claimTenantId, tenantInfo.Id, StringComparison.OrdinalIgnoreCase)` -> calls `context.Fail("Token tenant mismatch.")` and stores failure reason item `"TENANT_TOKEN_MISMATCH"` so `OnChallenge` emits a localized `401 ProblemDetails` (`errorCode = "TENANT_TOKEN_MISMATCH"`).
  * **Platform Context (`tenantInfo is null`)**:
    * If `context.Principal` contains a `"tenant_id"` claim -> calls `context.Fail("Tenant token cannot be used in platform context.")` (`401` with `errorCode = "TENANT_TOKEN_MISMATCH"`).
    * (Tokens without `"tenant_id"` and carrying platform claims will be issued and validated for Platform Admins in Sub-project 2).
* **`AuthCookieHelper`**:
  * Keeps `CookieOptions.Domain = null` explicitly (RFC 6265 Host-Only cookie) so cookies set on `school-a.examprism.com` are never transmitted to `school-b.examprism.com` or `examprism.com`, and cookies set on `examprism.com` are never transmitted to `*.examprism.com`.

### 6.2. Tenant-Scoped Caching (`HybridCacheService`)
* `HybridCacheService` (singleton) injects `IMultiTenantContextAccessor<ExamPrismTenantInfo>` (also singleton, `AsyncLocal`-backed in Finbuckle).
* Determines scope prefix:
  * Active tenant (`tenantInfo is { Status: TenantStatus.Active }`): prefix `"t:{tenantInfo.Id}:"`, automatic tenant umbrella tag `"t:{tenantInfo.Id}"`.
  * Platform context (`tenantInfo is null`): prefix `"platform:"`.
* Key transformation: `"{scopePrefix}{key}"`.
* Tag transformation: every caller tag `tag` becomes `"{scopePrefix}{tag}"` (plus umbrella tag `"t:{tenantInfo.Id}"` in tenant context, enabling one-call whole-tenant cache eviction via `HybridCache.RemoveByTagAsync($"t:{tenantId}")`).
* `ICacheService` public interface remains 100% unchanged.

### 6.3. Tenant-Scoped Rate Limiting (`RateLimitingSetup`)
* Reads active tenant from `httpContext.GetMultiTenantContext<ExamPrismTenantInfo>().TenantInfo`:
  * `var scope = tenantInfo is { Status: TenantStatus.Active } ? $"t:{tenantInfo.Id}" : "platform";`
  * Auth policy partition key: `$"{scope}:auth:ip:{ip}"`
  * Global policy partition key: `$"{scope}:global:user:{userId}"` or `$"{scope}:global:ip:{ip}"`
* Ensures identical `UserId` values across different Tenant DBs or high traffic on one tenant never collide in rate-limiter buckets.

### 6.4. Health Checks (`DependencyInjection`)
* Replace `.AddDbContextCheck<AppDbContext>(name: "database")` with `.AddDbContextCheck<CatalogDbContext>(name: "catalog_db")` (alongside `"redis"` when `ConnectionStrings:Redis` is configured).
* `/health` succeeds on root/platform hosts (`http://localhost/health`, Kubernetes probes) without requiring a tenant subdomain or opening connections to >500 tenant databases.

### 6.5. Wolverine Background Message Bridge (`TenantWolverineMiddleware`)
* Registered in `Program.cs` via `opts.Policies.AddMiddleware<TenantWolverineMiddleware>()`.
* `BeforeAsync(Envelope envelope, IMultiTenantContextAccessor<ExamPrismTenantInfo> accessor, IMultiTenantContextSetter setter, IMultiTenantStore<ExamPrismTenantInfo> store)`:
  1. If `string.IsNullOrWhiteSpace(envelope.TenantId)` or `envelope.TenantId == "*DEFAULT*"` (Wolverine's default untenant constant): do nothing.
  2. If `accessor.MultiTenantContext.TenantInfo` is already populated and matches `envelope.TenantId` (by `Id` or `Identifier`, case-insensitive): do nothing (fast path for HTTP `bus.InvokeAsync`).
  3. Otherwise (background/async message via `DeliveryOptions { TenantId = ... }` or `bus.InvokeForTenantAsync(...)`):
     * Look up tenant via `Guid.TryParse(envelope.TenantId, ...)` -> `store.GetAsync(id)` else `store.GetByIdentifierAsync(envelope.TenantId.ToLowerInvariant())`.
     * If `tenantInfo is null || tenantInfo.Status != TenantStatus.Active`: throw `InvalidOperationException($"Cannot execute message for inactive or unknown tenant '{envelope.TenantId}'.")`.
     * Set `setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo)` so scoped `AppDbContext`, `UserManager<ApplicationUser>`, `ITenantContext`, and `ICacheService` work identically to an HTTP request.

---

## 7. HTTP Pipeline Order & Localized Error Contracts

### 7.1. Middleware Order (`Program.cs`)
```csharp
app.UseRequestLocalization();
app.UseExceptionHandler();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(...);
}

app.UseMultiTenant();
app.UseMiddleware<TenantAccessGuardMiddleware>();

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapEndpoints();
```

### 7.2. Standardized Error Responses (`ProblemDetails` + `SharedResources`)
All multi-tenancy rejections return `application/problem+json` with `Extensions["traceId"]` and `Extensions["errorCode"]`:

| Error Code | HTTP Status | Extra Header | Resource Key | `en-US` (`SharedResources.resx`) | `vi-VN` (`SharedResources.vi.resx`) |
|---|---|---|---|---|---|
| `TENANT_NOT_FOUND` | `404 Not Found` | — | `TenantNotFound` | `The requested organization was not found.` | `Không tìm thấy tổ chức theo tên miền này.` |
| `TENANT_PROVISIONING` | `503 Service Unavailable` | `Retry-After: 5` | `TenantProvisioning` | `This organization workspace is currently being provisioned. Please try again shortly.` | `Không gian làm việc của tổ chức đang được khởi tạo. Vui lòng thử lại sau giây lát.` |
| `TENANT_SUSPENDED` | `403 Forbidden` | — | `TenantSuspended` | `This organization workspace has been suspended.` | `Không gian làm việc của tổ chức đã bị tạm khóa.` |
| `TENANT_UNAVAILABLE` | `403 Forbidden` | — | `TenantUnavailable` | `This organization workspace is currently unavailable.` | `Không gian làm việc của tổ chức hiện không khả dụng.` |
| `TENANT_REQUIRED` | `400 Bad Request` | — | `TenantRequired` | `A valid organization subdomain is required for this endpoint.` | `Yêu cầu truy cập qua tên miền phụ (subdomain) của tổ chức.` |
| `PLATFORM_ONLY` | `404 Not Found` | — | `PlatformOnly` | `This endpoint is only available on the platform domain.` | `Đường dẫn này chỉ khả dụng trên tên miền hệ thống.` |
| `TENANT_TOKEN_MISMATCH` | `401 Unauthorized` | — | `TenantTokenMismatch` | `The authentication token does not belong to this organization.` | `Mã xác thực không thuộc về tổ chức này.` |

---

## 8. Testing Strategy & Existing Test Suite Adaptation

### 8.1. Unit Tests
* **`SubdomainParserTests`**: Covers root `BaseDomains` (`examprism.com`, `localhost`), `ReservedSubdomains` (`www`, `admin`, `api`), valid tenant slugs (`school-a.examprism.com`, `school-a.localhost:5000`), invalid multi-level subdomains (`a.b.examprism.com`), IP literals, and `X-Tenant` fallback (enabled only in Development on root host; ignored in Production; never overrides explicit subdomain).
* **`DataProtectionConnectionStringProtectorTests`**: Round-trip encryption/decryption and tamper rejection.
* **`TenantConnectionStringNormalizerTests`**: Verifies `MaxPoolSize`, `MinPoolSize = 0`, `ConnectionIdleLifetime`, `ConnectionPruningInterval`, and preservation of host/database/credentials.
* **`HybridCacheServiceTenantIsolationTests`**: Verifies same cache key `"exam:1"` and same tag `"exams"` do not collide across Tenant A, Tenant B, and Platform Context.
* **`TokenServiceTests` & `JwtTenantValidationTests`**: Verifies `"tenant_id"` claim emission, rejection when `ITenantContext` is absent, and `OnTokenValidated` rejection of cross-tenant or tenant-on-platform tokens.
* **`ArchitectureTests`**: Confirms `ExamPrism.Features` remains free of HTTP/Finbuckle types and all handlers/endpoints still pass strict architectural rules.

### 8.2. Multi-Database Integration Tests (`CustomWebApplicationFactory`)
Using **one** `Testcontainers.PostgreSql` container (`postgres:17-alpine`) for fast execution while maintaining physical database isolation:
1. On `InitializeAsync()`, create three physical PostgreSQL databases inside the container:
   * `examprism_catalog_test` -> migrated via `CatalogDbContext.Database.MigrateAsync()`.
   * `examprism_tenant_a` -> migrated via `AppDbContext.Database.MigrateAsync()`.
   * `examprism_tenant_b` -> migrated via `AppDbContext.Database.MigrateAsync()`.
2. Seed `CatalogDbContext.Tenants` with:
   * `"tenant-a"` (`Active`, encrypted connection string to `examprism_tenant_a`)
   * `"tenant-b"` (`Active`, encrypted connection string to `examprism_tenant_b`)
   * `"provisioning-org"` (`Provisioning`, `EncryptedConnectionString = null`)
   * `"suspended-org"` (`Suspended`, encrypted connection string)
   * `"failed-org"` (`Failed`, `FailureReason = "Migration timeout"`)
3. Set `ClientOptions.BaseAddress = new Uri("http://tenant-a.localhost")` so all existing Auth, Validation, Exception, and Rate-Limiting integration tests execute against an active tenant out of the box.
4. Provide factory helpers:
   * `CreateTenantClient(string slug)` -> `BaseAddress = new Uri($"http://{slug}.localhost")`
   * `CreatePlatformClient()` -> `BaseAddress = new Uri("http://localhost")`
5. **End-to-End Multi-Tenant Integration Test Suite (`MultiTenancyIntegrationTests`)**:
   * **Physical DB Isolation**: Register `alice@school.edu` on `tenant-a.localhost` and `alice@school.edu` (different password) on `tenant-b.localhost` -> both succeed independently in separate DBs; login on each verifies isolated credentials and user IDs.
   * **Cross-Tenant Token Rejection**: Access token (Bearer header AND `access_token` cookie) issued on `tenant-a.localhost` returns `401` (`TENANT_TOKEN_MISMATCH`) when sent to `tenant-b.localhost/api/auth/me` or `localhost`.
   * **Lifecycle Guard Responses**: Requests to `unknown.localhost` (`404 TENANT_NOT_FOUND`), `provisioning-org.localhost` (`503 TENANT_PROVISIONING` + `Retry-After: 5`), `suspended-org.localhost` (`403 TENANT_SUSPENDED`), `failed-org.localhost` (`403 TENANT_UNAVAILABLE`), localized in both `vi-VN` and `en-US`.
   * **Boundary Guards**: Calling `http://localhost/api/auth/login` (Platform Context) returns `400 TENANT_REQUIRED`; calling `http://localhost/health` returns `200 Healthy`; calling a test `.RequirePlatform()` endpoint from `tenant-a.localhost` returns `404 PLATFORM_ONLY`.
   * **Rate Limit & Cache Isolation**: Exhausting rate limit on `tenant-a.localhost` does not throttle `tenant-b.localhost`.
   * **Wolverine Background Bridge**: `bus.InvokeForTenantAsync("tenant-b", ...)` / `DeliveryOptions { TenantId = "tenant-b" }` resolves `examprism_tenant_b` `AppDbContext` without an `HttpContext`.

### 8.3. Adapting Existing Infrastructure Tests
* **`DependencyInjectionTests`**: Update configuration fixture key to `ConnectionStrings:CatalogConnection`, assert `"catalog_db"` health check registration, assert `CatalogDbContext` + `AppDbContext` + Finbuckle `IMultiTenantStore<ExamPrismTenantInfo>` + `ITenantContext` registrations, and add a test proving resolving `AppDbContext` without a tenant throws `InvalidOperationException`.
* **`DatabaseIntegrationTests`**: Verify `CatalogDbContext.Database.CanConnectAsync()` directly, verify `AppDbContext.Database.CanConnectAsync()` inside a tenant-initialized scope, and test unreachable `ConnectionStrings:CatalogConnection` returning `503 Unhealthy` on `/health`.
