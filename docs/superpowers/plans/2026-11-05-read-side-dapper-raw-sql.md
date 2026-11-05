# Read-Side Dapper Raw SQL via DbContext & Query Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Execute all read-side queries directly with parameterized raw SQL via Dapper over EF Core `DbContext` connections (`ReadOnlyCatalogDbContext`, `ReadOnlyAppDbContext`, and `DbContext`), and remove all read-side `ISpecification` / `IQueryable` abstractions while preserving `ISpecification<TEntity>` and `IDbQuery<in TContext, TResult>` strictly for the write side.

**Architecture:** `DapperCommandExecutor` wraps `db.Database.GetDbConnection()`, inherits active `DbTransaction` and command timeouts, passes `CancellationToken` via `CommandDefinition`, and reports execution latency to `SlowQueryTelemetryInterceptor.RecordIfSlow`. `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` become pure Dapper raw SQL gateways (removing `IQueryable<T>` and `ISpecification` overloads), while `ISpecification<TEntity>` and `IDbQuery<in TContext, TResult> where TContext : DbContext` remain exclusively on `DbContext` for write-side aggregate loading and commands.

**Tech Stack:** .NET 10, C# 14, Entity Framework Core 10 (`Npgsql.EntityFrameworkCore.PostgreSQL`), Dapper 2.1.66, WolverineFx, xUnit, FluentAssertions, Testcontainers (PostgreSQL).

**Spec:** `docs/superpowers/specs/2026-11-05-read-side-dapper-raw-sql-design.md`

## Global Constraints

- Single-project modular structure in `apps/backend/src/ExamPrism.csproj` with central package management in `apps/backend/Directory.Packages.props` (`<PackageVersion Include="Dapper" Version="2.1.66" />`).
- `Common.Domain` and `ExamPrism.Domain` must have zero references to `ExamPrism.Infrastructure`, `ExamPrism.Features`, `ExamPrism.Endpoints`, `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `Wolverine`, `Orleans`, `StackExchange.Redis`, or `Dapper`.
- `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` must not expose any public property or method returning `IQueryable` / `IQueryable<>` or accepting `ISpecification<>`.
- Never dispose the `DbConnection` returned by `db.Database.GetDbConnection()` inside `DapperCommandExecutor`; `DbContext` owns the connection lifecycle.
- Preserve `SlowQueryTelemetryInterceptor.RecordIfSlow(sql, elapsed)` telemetry across all Dapper raw SQL methods (`QueryAsync`, `QueryFirstOrDefaultAsync`, `QuerySingleOrDefaultAsync`, `QuerySingleAsync`, `ExecuteScalarAsync`, `QueryMultipleAsync`).

## Review Focus

1. **String-to-enum mapping for `TenantStatus` in Dapper**: PostgreSQL stores `"Tenants"."Status"` as `varchar(32)`. `StringEnumTypeHandler<TenantStatus>` must be registered via `DapperTypeHandlers.EnsureRegistered()` before queries run so positional DTO records (`TenantSummaryItem`, `TenantDetailResult`, `GetTenantProvisioningStatusResult`) and enum parameters map cleanly between `string` and `TenantStatus`.
2. **Nullable SQL filter parameters in PostgreSQL (`42P08`)**: In `ListTenantsHandler`, passing `string? Status = query.Status?.ToString()` and `string? Pattern = ...` ensures Dapper binds `DbType.String` even when `null`, preventing PostgreSQL indeterminate parameter type errors on `@Status IS NULL` and `@Pattern IS NULL`.
3. **Slow-query telemetry recording on Dapper queries**: `DapperCommandExecutor` must measure elapsed time in a `try/finally` block and invoke `SlowQueryTelemetryInterceptor.RecordIfSlow(sql, elapsed)` so slow Dapper queries trigger the same warning log and OpenTelemetry counter as EF Core commands.
4. **Connection reuse across consecutive Dapper queries on the same `ReadOnly*DbContext`**: Executing multiple Dapper queries sequentially on the same `ReadOnlyCatalogDbContext` or `CatalogDbContext` instance must leave the underlying `DbContext` connection usable until the context itself is disposed.
5. **Architectural boundary enforcement on `ReadOnly*DbContext`**: `ArchitectureTests` must fail if any `IQueryable` or `ISpecification` member is reintroduced on `ReadOnlyCatalogDbContext` or `ReadOnlyAppDbContext`, or if `Dapper` is referenced from `Domain`.

---

### Task 1: Dapper Raw SQL Infrastructure, ReadOnly*DbContext Gateway & Query Interface Cleanup

**Files:**
- Modify: `apps/backend/Directory.Packages.props`
- Modify: `apps/backend/src/ExamPrism.csproj`
- Create: `apps/backend/src/Infrastructure/Persistence/DapperTypeHandlers.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/DapperCommandExecutor.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/ReadOnlyCatalogDbContext.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/ReadOnlyAppDbContext.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/PersistenceSetup.cs:70-126`
- Modify: `apps/backend/src/Common/Domain/Specifications/ISpecification.cs`
- Modify: `apps/backend/src/Common/Domain/Specifications/Specification.cs:195-205`
- Modify: `apps/backend/src/Infrastructure/Persistence/SpecificationEvaluator.cs:90-101`
- Modify: `apps/backend/src/Infrastructure/Persistence/DbContextQueryExtensions.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Domain/DddPrimitivesTests.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/PersistenceHighLoadTests.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DddPersistenceIntegrationTests.cs`

**Interfaces:**
- Consumes:
  - `SlowQueryTelemetryInterceptor.RecordIfSlow(string? commandText, TimeSpan duration, string? tenantId = null)` (`ExamPrism.Infrastructure.Persistence`)
  - `ISpecification<TEntity>`, `Specification<TEntity>` (`Common.Domain.Specifications`)
  - `IDbQuery<in TContext, TResult>` (`Common.Persistence`)
- Produces:
  - `DapperTypeHandlers.EnsureRegistered()` and `StringEnumTypeHandler<TEnum> : SqlMapper.TypeHandler<TEnum>` in `ExamPrism.Infrastructure.Persistence`
  - `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` Dapper methods:
    - `Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
    - `Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
    - `Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
    - `Task<T> QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
    - `Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
    - `Task<TResult> QueryMultipleAsync<TResult>(string sql, Func<SqlMapper.GridReader, Task<TResult>> map, object? param = null, CancellationToken ct = default)`
  - Matching extension methods on `DbContext` in `DbContextQueryExtensions`
  - Write-side `DbContextQueryExtensions`:
    - `Task<TResult> QueryAsync<TContext, TResult>(this TContext db, IDbQuery<TContext, TResult> query, CancellationToken ct = default) where TContext : DbContext`
    - `QueryFirstOrDefaultAsync<TEntity>`, `QueryListAsync<TEntity>`, `QueryAnyAsync<TEntity>`, `QueryCountAsync<TEntity>` on `DbContext` only.

- [ ] **Step 1: Add `Dapper` package and write failing architecture, unit, and integration tests**

1. Add `<PackageVersion Include="Dapper" Version="2.1.66" />` to `apps/backend/Directory.Packages.props` and `<PackageReference Include="Dapper" />` to `apps/backend/src/ExamPrism.csproj`.
2. In `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`:
   - Add `"Dapper"` to `forbiddenPrefixes` in `TypesInDomain_MustNotReferenceInfrastructureFeaturesEndpointsOrExternalFrameworks`.
   - Add test `ReadOnlyDbContexts_MustNotExposeIQueryableOrAcceptSpecifications`:
     ```csharp
     [Fact]
     public void ReadOnlyDbContexts_MustNotExposeIQueryableOrAcceptSpecifications()
     {
         var readOnlyTypes = new[] { typeof(ReadOnlyCatalogDbContext), typeof(ReadOnlyAppDbContext) };

         foreach (var type in readOnlyTypes)
         {
             var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
             foreach (var prop in properties)
             {
                 typeof(IQueryable).IsAssignableFrom(prop.PropertyType).Should().BeFalse(
                     $"{type.Name}.{prop.Name} must not expose IQueryable; read side uses Dapper raw SQL");
             }

             var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
             foreach (var method in methods)
             {
                 typeof(IQueryable).IsAssignableFrom(method.ReturnType).Should().BeFalse(
                     $"{type.Name}.{method.Name} must not return IQueryable");

                 foreach (var param in method.GetParameters())
                 {
                     var paramType = param.ParameterType.IsGenericType
                         ? param.ParameterType.GetGenericTypeDefinition()
                         : param.ParameterType;
                     (paramType == typeof(ISpecification<>)).Should().BeFalse(
                         $"{type.Name}.{method.Name} must not accept ISpecification; specifications are write-side only");
                 }
             }
         }
     }
     ```
3. In `apps/backend/tests/ExamPrism.Tests/Domain/DddPrimitivesTests.cs`:
   - Replace `ProductDtoSpec` (`Specification<ProductItem, string>`) with a non-generic `PagedProductSpec : Specification<ProductItem>` (testing `ApplyOrderBy`, `ApplyPaging`, `WithSplitQuery`, `WithIgnoreQueryFilters` without `Select`), and add an assertion that `typeof(ISpecification<>).Assembly.GetType("Common.Domain.Specifications.ISpecification`2")` is `null`.
4. In `apps/backend/tests/ExamPrism.Tests/Infrastructure/PersistenceHighLoadTests.cs`:
   - Update `ReadOnlyCatalogDbContext_And_ReadOnlyAppDbContext_EnforceNoTracking_AndFallbackToPrimaryConnectionWhenReplicaNotConfigured` to remove `.Tenants`, `.Users`, `.PlatformRefreshTokens`, `.RefreshTokens` property assertions and assert `readOnlyCatalog.DatabaseContext` and `readOnlyApp.DatabaseContext` are non-null with `QueryTrackingBehavior.NoTracking`.
5. In `apps/backend/tests/ExamPrism.Tests/Infrastructure/DddPersistenceIntegrationTests.cs`:
   - Replace `DbContextQueryExtensions_ExecutesIDbQuery_AndSpecificationMethods_OnWriteAndReadOnlyContexts` with two focused integration tests:
     - `WriteSideDbContext_ExecutesIDbQuery_AndEntitySpecifications`: verifies `db.QueryAsync(new CustomCatalogTenantCountQuery(prefix))`, `db.QueryFirstOrDefaultAsync(firstSpec)`, `db.QueryListAsync(firstSpec)`, `db.QueryAnyAsync(pagedSpec)`, and `db.QueryCountAsync(pagedSpec)` (ignoring `Skip`/`Take`).
     - `ReadOnlyCatalogDbContext_And_DbContext_ExecuteDapperRawSql_MapStringEnums_ReuseConnection_AndRecordSlowQueries`: verifies `QueryAsync<TenantSummaryDto>`, `QueryFirstOrDefaultAsync<TenantSummaryDto>`, `QuerySingleOrDefaultAsync<TenantSummaryDto>`, `QuerySingleAsync<int>`, `ExecuteScalarAsync<bool>`, and `QueryMultipleAsync` on `ReadOnlyCatalogDbContext` and `CatalogDbContext` (including `TenantStatus` mapping, sequential queries reusing the connection, and `SlowQueryTelemetryInterceptor` recording slow Dapper queries when `SlowQueryThresholdMs = 0`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ReadOnlyDbContexts_MustNotExposeIQueryableOrAcceptSpecifications|FullyQualifiedName~DddPersistenceIntegrationTests"`
Expected: FAIL (compilation error on missing Dapper methods on `ReadOnlyCatalogDbContext` and failing architecture test on exposed `IQueryable` properties).

- [ ] **Step 3: Implement Dapper execution infrastructure and clean up read-side specification/query interfaces**

1. Create `apps/backend/src/Infrastructure/Persistence/DapperTypeHandlers.cs` with `StringEnumTypeHandler<TEnum>` and `DapperTypeHandlers.EnsureRegistered()` as specified in Spec Section 2.2.
2. Create `apps/backend/src/Infrastructure/Persistence/DapperCommandExecutor.cs` with `QueryAsync<T>`, `QueryFirstOrDefaultAsync<T>`, `QuerySingleOrDefaultAsync<T>`, `QuerySingleAsync<T>`, `ExecuteScalarAsync<T>`, and `QueryMultipleAsync<TResult>` as specified in Spec Section 2.3.
3. Update `apps/backend/src/Infrastructure/Persistence/ReadOnlyCatalogDbContext.cs` and `apps/backend/src/Infrastructure/Persistence/ReadOnlyAppDbContext.cs`:
   - Accept optional `SlowQueryTelemetryInterceptor? slowQueryInterceptor = null` in constructors.
   - Remove `Tenants`, `Users`, `PlatformRefreshTokens`, `RefreshTokens`, `Set<TEntity>()`, and `ChangeTracker`.
   - Expose the 6 Dapper raw SQL methods delegating to `DapperCommandExecutor` with `_inner` and `_slowQueryInterceptor`.
4. Update `apps/backend/src/Infrastructure/Persistence/PersistenceSetup.cs`:
   - Call `DapperTypeHandlers.EnsureRegistered()` in `AddPersistence`.
   - Pass `interceptor` into `new ReadOnlyCatalogDbContext(inner, interceptor)` and `new ReadOnlyAppDbContext(inner, interceptor)`.
5. Clean up Specification and Query files:
   - Remove `ISpecification<TEntity, TResult>` from `apps/backend/src/Common/Domain/Specifications/ISpecification.cs`.
   - Remove `Specification<TEntity, TResult>` from `apps/backend/src/Common/Domain/Specifications/Specification.cs`.
   - Remove `GetQuery<TEntity, TResult>` from `apps/backend/src/Infrastructure/Persistence/SpecificationEvaluator.cs`.
   - Update `apps/backend/src/Infrastructure/Persistence/DbContextQueryExtensions.cs`:
     - Constrain `QueryAsync<TContext, TResult>(this TContext db, IDbQuery<TContext, TResult> query, CancellationToken ct = default)` with `where TContext : DbContext`.
     - Keep only `QueryFirstOrDefaultAsync<TEntity>`, `QueryListAsync<TEntity>`, `QueryAnyAsync<TEntity>`, and `QueryCountAsync<TEntity>` on `DbContext`.
     - Remove all `ISpecification<TEntity, TResult>` overloads and all `ReadOnlyCatalogDbContext` / `ReadOnlyAppDbContext` `ISpecification` overloads.
     - Add the 6 Dapper raw SQL extension methods (`QueryAsync<T>`, `QueryFirstOrDefaultAsync<T>`, `QuerySingleOrDefaultAsync<T>`, `QuerySingleAsync<T>`, `ExecuteScalarAsync<T>`, `QueryMultipleAsync<TResult>`) on `DbContext` delegating to `DapperCommandExecutor`.

- [ ] **Step 4: Run tests to verify Task 1 passes**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~DddPrimitivesTests|FullyQualifiedName~PersistenceHighLoadTests|FullyQualifiedName~DddPersistenceIntegrationTests"`
Expected: PASS

---

### Task 2: Refactor Tenant Read-Side Query Handlers to `ReadOnlyCatalogDbContext` + Dapper Raw SQL

**Files:**
- Modify: `apps/backend/src/Features/Platform/Tenants/ListTenantsHandler.cs`
- Modify: `apps/backend/src/Features/Platform/Tenants/GetTenantByIdHandler.cs`
- Modify: `apps/backend/src/Features/Tenants/CheckTenantSlugHandler.cs`
- Modify: `apps/backend/src/Features/Tenants/GetTenantProvisioningStatusHandler.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Features/PlatformTenantHandlersTests.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Features/Tenants/TenantOnboardingHandlersTests.cs`

**Interfaces:**
- Consumes (from Task 1):
  - `ReadOnlyCatalogDbContext.QueryMultipleAsync<TResult>(string sql, Func<SqlMapper.GridReader, Task<TResult>> map, object? param = null, CancellationToken ct = default)`
  - `ReadOnlyCatalogDbContext.QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
  - `ReadOnlyCatalogDbContext.ExecuteScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
- Produces:
  - `ListTenantsHandler(ReadOnlyCatalogDbContext db)` (`ListTenantsDbQuery` removed)
  - `GetTenantByIdHandler(ReadOnlyCatalogDbContext db)`
  - `CheckTenantSlugHandler(ReadOnlyCatalogDbContext db, IOptions<MultiTenancyOptions> options)`
  - `GetTenantProvisioningStatusHandler(ReadOnlyCatalogDbContext db)`

- [ ] **Step 1: Update handler integration tests to construct `ReadOnlyCatalogDbContext` and assert `ListTenantsDbQuery` is removed**

1. In `apps/backend/tests/ExamPrism.Tests/Features/PlatformTenantHandlersTests.cs`:
   - Pass `new ReadOnlyCatalogDbContext(db)` to `new ListTenantsHandler(new ReadOnlyCatalogDbContext(db))` and `new GetTenantByIdHandler(new ReadOnlyCatalogDbContext(db))`.
   - Also assert `typeof(ListTenantsHandler).Assembly.GetType("ExamPrism.Features.Platform.Tenants.ListTenantsDbQuery")` is `null` and test `ListTenantsHandler` with both `Status: null, Search: null` (unfiltered nullable parameters) and filtered parameters.
2. In `apps/backend/tests/ExamPrism.Tests/Features/Tenants/TenantOnboardingHandlersTests.cs`:
   - Pass `new ReadOnlyCatalogDbContext(db)` to `new CheckTenantSlugHandler(new ReadOnlyCatalogDbContext(db), options)` and `new GetTenantProvisioningStatusHandler(new ReadOnlyCatalogDbContext(db))`.

- [ ] **Step 2: Run handler tests to verify they fail**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~PlatformTenantHandlersTests|FullyQualifiedName~TenantOnboardingHandlersTests"`
Expected: FAIL (constructor parameter type mismatch on `ListTenantsHandler`, `GetTenantByIdHandler`, `CheckTenantSlugHandler`, `GetTenantProvisioningStatusHandler`).

- [ ] **Step 3: Refactor the 4 read-side handlers to inject `ReadOnlyCatalogDbContext` and execute raw SQL via Dapper**

1. In `apps/backend/src/Features/Platform/Tenants/ListTenantsHandler.cs`:
   - Delete `ListTenantsDbQuery`.
   - Change `ListTenantsHandler` constructor to `ListTenantsHandler(ReadOnlyCatalogDbContext db)`.
   - Normalize `var page = query.Page < 1 ? 1 : query.Page;`, `var pageSize = query.PageSize < 1 ? 20 : query.PageSize;`, `string? status = query.Status?.ToString();`, `string? pattern = string.IsNullOrWhiteSpace(query.Search) ? null : $"%{query.Search.Trim()}%";`.
   - Execute `db.QueryMultipleAsync` with the parameterized `COUNT(*)` + paged `SELECT` query from Spec Section 4.1 (`CAST(@Status AS varchar(32)) IS NULL OR "Status" = @Status` and `CAST(@Pattern AS text) IS NULL OR "Slug" ILIKE @Pattern OR "Name" ILIKE @Pattern`) and return `new ListTenantsResult(items, totalCount, page, pageSize)`.
2. In `apps/backend/src/Features/Platform/Tenants/GetTenantByIdHandler.cs`:
   - Change constructor to `GetTenantByIdHandler(ReadOnlyCatalogDbContext db)`.
   - Execute `db.QueryFirstOrDefaultAsync<TenantDetailResult>(sql, new { query.Id }, ct)` using the SQL from Spec Section 4.2; return `TenantErrors.TenantNotFound()` when `null`.
3. In `apps/backend/src/Features/Tenants/CheckTenantSlugHandler.cs`:
   - Change constructor to `CheckTenantSlugHandler(ReadOnlyCatalogDbContext db, IOptions<MultiTenancyOptions> options)`.
   - Execute `var isTaken = await db.ExecuteScalarAsync<bool>(sql, new { Slug = slug }, ct);` using the `SELECT EXISTS (...)` SQL from Spec Section 4.3.
4. In `apps/backend/src/Features/Tenants/GetTenantProvisioningStatusHandler.cs`:
   - Change constructor to `GetTenantProvisioningStatusHandler(ReadOnlyCatalogDbContext db)`.
   - Execute `db.QueryFirstOrDefaultAsync<GetTenantProvisioningStatusResult>(sql, new { Slug = slug }, ct)` using the SQL from Spec Section 4.4; return `TenantErrors.TenantNotFound()` when `null`.

- [ ] **Step 4: Run full test suite to verify everything passes**

Run: `dotnet test apps/backend/ExamPrism.slnx`
Expected: PASS (all unit, architecture, and integration tests pass with 0 failures).

