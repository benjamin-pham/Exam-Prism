# Native Dapper Raw SQL via DbContext & Read-Only Wrapper Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove all `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` wrappers, delete custom Dapper forwarder extensions, and refactor all read handlers to inject `CatalogDbContext` (or `AppDbContext`) and execute native Dapper directly on `db.Database.GetDbConnection()`.

**Architecture:** Features inject `CatalogDbContext` / `AppDbContext` directly for both reads and writes. For read queries, handlers call `var connection = db.Database.GetDbConnection()` and execute native Dapper extension methods with `CommandDefinition` for parameter binding and `CancellationToken`. EF Core owns the connection lifecycle. `Common.Domain` remains 100% ORM/Dapper agnostic.

**Tech Stack:** .NET 10, C# 14, Entity Framework Core 10, Dapper 2.1.66, PostgreSQL, xUnit, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-11-05-read-side-dapper-raw-sql-design.md`

## Global Constraints

- Do not move `AppDbContext` or `CatalogDbContext` to `Common`; they remain in `Infrastructure/Persistence/`.
- Never dispose the `DbConnection` returned by `db.Database.GetDbConnection()`; `DbContext` owns the connection lifecycle.
- Keep `ArchitectureTests` pure: `Domain` must not reference `Dapper` or `Infrastructure`.
- Remove all traces of `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` from production code and tests.

## Review Focus

1. **Enum mapping with native Dapper**: PostgreSQL stores `"Tenants"."Status"` as `varchar(32)`. `DapperTypeHandlers.EnsureRegistered()` must be invoked in `PersistenceSetup.AddPersistence` so `connection.QueryAsync` maps `TenantStatus` to/from strings seamlessly.
2. **Nullable parameters in PostgreSQL queries**: In `ListTenantsHandler`, use explicit SQL casts (`CAST(@Status AS varchar(32)) IS NULL OR "Status" = @Status` and `CAST(@Pattern AS text) IS NULL OR "Slug" ILIKE @Pattern OR "Name" ILIKE @Pattern`) so PostgreSQL never encounters 42P08 indeterminate type errors when parameters are null.
3. **Cancellation token propagation via `CommandDefinition`**: Native Dapper calls in handlers must wrap queries in `new CommandDefinition(sql, param, cancellationToken: ct)`.
4. **Clean DI container without orphaned read-only services**: Ensure `PersistenceSetup.cs` completely removes `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` registrations.

---

### Task 1: Delete Read-Only Wrappers, Clean Up DI and DbContextQueryExtensions

**Files:**
- Delete: `apps/backend/src/Infrastructure/Persistence/ReadOnlyCatalogDbContext.cs`
- Delete: `apps/backend/src/Infrastructure/Persistence/ReadOnlyAppDbContext.cs`
- Delete: `apps/backend/src/Infrastructure/Persistence/DapperCommandExecutor.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/PersistenceSetup.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/DbContextQueryExtensions.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/PersistenceHighLoadTests.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DddPersistenceIntegrationTests.cs`

**Interfaces:**
- Consumes:
  - `DapperTypeHandlers.EnsureRegistered()` (`ExamPrism.Infrastructure.Persistence`)
  - `ISpecification<TEntity>`, `Specification<TEntity>` (`Common.Domain.Specifications`)
  - `IDbQuery<in TContext, TResult>` (`Common.Persistence`)
- Produces:
  - Cleaned `DbContextQueryExtensions` with only write-side extensions on `DbContext`:
    - `Task<TResult> QueryAsync<TContext, TResult>(this TContext db, IDbQuery<TContext, TResult> query, CancellationToken ct = default) where TContext : DbContext`
    - `QueryFirstOrDefaultAsync<TEntity>`, `QueryListAsync<TEntity>`, `QueryAnyAsync<TEntity>`, `QueryCountAsync<TEntity>` on `DbContext`
  - Zero read-only wrapper classes or forwarder methods in `Infrastructure/Persistence/`

- [x] **Step 1: Write failing tests asserting removal of read-only wrappers and verifying native Dapper on `db.Database.GetDbConnection()`**

1. In `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`:
   - Replace `ReadOnlyDbContexts_MustNotExposeIQueryableOrAcceptSpecifications` with an assertion that `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` types do not exist in the assembly.
2. In `apps/backend/tests/ExamPrism.Tests/Infrastructure/PersistenceHighLoadTests.cs`:
   - Remove references to `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` and verify `CatalogDbContext` and `AppDbContext` resolution.
3. In `apps/backend/tests/ExamPrism.Tests/Infrastructure/DddPersistenceIntegrationTests.cs`:
   - Update `ReadOnlyCatalogDbContext_And_DbContext_ExecuteDapperRawSql_MapStringEnums_ReuseConnection_AndRecordSlowQueries` to test native Dapper directly on `db.Database.GetDbConnection()`:
     ```csharp
     var connection = db.Database.GetDbConnection();
     var list = await connection.QueryAsync<TenantSummaryDto>(
         new CommandDefinition("SELECT \"Id\", \"Slug\", \"Name\", \"Status\" FROM \"Tenants\" WHERE \"Slug\" LIKE @Pattern ORDER BY \"Slug\"", new { Pattern = $"{prefix}%" }));
     ```

- [x] **Step 2: Run tests to verify they fail**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~PersistenceHighLoadTests|FullyQualifiedName~DddPersistenceIntegrationTests"`
Expected: FAIL (compilation errors on obsolete read-only references).

- [x] **Step 3: Remove wrappers and clean up `PersistenceSetup` & `DbContextQueryExtensions`**

1. Delete `ReadOnlyCatalogDbContext.cs`, `ReadOnlyAppDbContext.cs`, and `DapperCommandExecutor.cs`.
2. In `apps/backend/src/Infrastructure/Persistence/PersistenceSetup.cs`:
   - Remove the `services.AddScoped<ReadOnlyCatalogDbContext>(...)` and `services.AddScoped<ReadOnlyAppDbContext>(...)` blocks.
3. In `apps/backend/src/Infrastructure/Persistence/DbContextQueryExtensions.cs`:
   - Remove all custom Dapper forwarder extension methods (`QueryAsync<T>`, `QueryFirstOrDefaultAsync<T>`, `ExecuteScalarAsync<T>`, `QueryMultipleAsync`).
   - Retain only `QueryAsync<TContext, TResult>` (`IDbQuery`) and the 4 `ISpecification<TEntity>` methods on `DbContext`.

- [x] **Step 4: Run tests to verify Task 1 passes**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~PersistenceHighLoadTests|FullyQualifiedName~DddPersistenceIntegrationTests"`
Expected: PASS

---

### Task 2: Refactor Read Handlers to Inject `CatalogDbContext` and Use Native Dapper

**Files:**
- Modify: `apps/backend/src/Features/Platform/Tenants/ListTenantsHandler.cs`
- Modify: `apps/backend/src/Features/Platform/Tenants/GetTenantByIdHandler.cs`
- Modify: `apps/backend/src/Features/Tenants/CheckTenantSlugHandler.cs`
- Modify: `apps/backend/src/Features/Tenants/GetTenantProvisioningStatusHandler.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Features/PlatformTenantHandlersTests.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Features/Tenants/TenantOnboardingHandlersTests.cs`

**Interfaces:**
- Consumes:
  - `CatalogDbContext` (`ExamPrism.Infrastructure.Persistence.Catalog`)
  - `db.Database.GetDbConnection()` (`Microsoft.EntityFrameworkCore`)
  - Native Dapper `connection.QueryAsync`, `connection.QueryFirstOrDefaultAsync`, `connection.QueryMultipleAsync`, `connection.ExecuteScalarAsync` (`Dapper`)
- Produces:
  - `ListTenantsHandler(CatalogDbContext db)`
  - `GetTenantByIdHandler(CatalogDbContext db)`
  - `CheckTenantSlugHandler(CatalogDbContext db, IOptions<MultiTenancyOptions> options)`
  - `GetTenantProvisioningStatusHandler(CatalogDbContext db)`

- [x] **Step 1: Update handler tests to pass `CatalogDbContext` directly**

1. In `PlatformTenantHandlersTests.cs`:
   - Pass `db` directly to `new ListTenantsHandler(db)` and `new GetTenantByIdHandler(db)`.
2. In `TenantOnboardingHandlersTests.cs`:
   - Pass `db` directly to `new CheckTenantSlugHandler(db, options)` and `new GetTenantProvisioningStatusHandler(db)`.

- [x] **Step 2: Run handler tests to verify they fail**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~PlatformTenantHandlersTests|FullyQualifiedName~TenantOnboardingHandlersTests"`
Expected: FAIL (constructor parameter mismatch expecting `ReadOnlyCatalogDbContext`).

- [x] **Step 3: Refactor handlers to inject `CatalogDbContext` and use native Dapper on `db.Database.GetDbConnection()`**

1. In `ListTenantsHandler.cs`:
   - Constructor `ListTenantsHandler(CatalogDbContext db)`.
   - `var connection = db.Database.GetDbConnection();`
   - `await using var grid = await connection.QueryMultipleAsync(new CommandDefinition(ListTenantsSql, param, cancellationToken: ct));`
2. In `GetTenantByIdHandler.cs`:
   - Constructor `GetTenantByIdHandler(CatalogDbContext db)`.
   - `var connection = db.Database.GetDbConnection();`
   - `var tenant = await connection.QueryFirstOrDefaultAsync<TenantDetailResult>(new CommandDefinition(GetTenantByIdSql, new { query.Id }, cancellationToken: ct));`
3. In `CheckTenantSlugHandler.cs`:
   - Constructor `CheckTenantSlugHandler(CatalogDbContext db, IOptions<MultiTenancyOptions> options)`.
   - `var connection = db.Database.GetDbConnection();`
   - `var isTaken = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(CheckSlugSql, new { Slug = slug }, cancellationToken: ct));`
4. In `GetTenantProvisioningStatusHandler.cs`:
   - Constructor `GetTenantProvisioningStatusHandler(CatalogDbContext db)`.
   - `var connection = db.Database.GetDbConnection();`
   - `var tenant = await connection.QueryFirstOrDefaultAsync<GetTenantProvisioningStatusResult>(new CommandDefinition(GetStatusSql, new { Slug = slug }, cancellationToken: ct));`

- [x] **Step 4: Run full test suite to verify everything passes**

Run: `dotnet test apps/backend/ExamPrism.slnx`
Expected: PASS (all tests green, 0 failures).
