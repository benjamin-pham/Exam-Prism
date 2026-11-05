# Design Specification: Read-Side Dapper Raw SQL via EF Core DbContext & Query Interface Cleanup

**Date:** 2026-11-05  
**Status:** Draft (Pending User Spec Review)  
**Scope:** Architectural Subsystem — Dapper Raw SQL Execution via EF Core `DbContext` Connections on the Read Side, Write-Side Specification & Query Object Cleanup, and Tenant Read Handler Refactoring  

---

## 1. Context, Goals, and Scope

ExamPrism currently uses an Entity Framework Core-based persistence layer with `CatalogDbContext` / `AppDbContext` on the write side and `ReadOnlyCatalogDbContext` / `ReadOnlyAppDbContext` on the read side, paired with `ISpecification<TEntity>`, `ISpecification<TEntity, TResult>`, and `IDbQuery<in TContext, TResult>`.

To achieve strict CQRS separation and optimal read performance without losing EF Core's connection pooling (`TenantDataSourceRegistry`), multi-tenant connection resolution, and read-replica routing:

1. **Read Side Uses Dapper Raw SQL via `DbContext` Connection**:
   * Add `Dapper` and execute parameterized raw SQL queries directly on the underlying `DbConnection` managed by EF Core (`db.Database.GetDbConnection()`), inheriting any active `DbTransaction` (`db.Database.CurrentTransaction?.GetDbTransaction()`) and configured command timeout (`db.Database.GetCommandTimeout()`).
   * Preserve slow query observability by measuring Dapper command execution duration and invoking `SlowQueryTelemetryInterceptor.RecordIfSlow(sql, elapsed)`.
   * Transform `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` into dedicated Dapper raw SQL gateways (removing all `IQueryable<T>` / `Set<TEntity>()` / `ChangeTracker` exposures while keeping inner `DbContext` lifetime and connection management).
   * Expose matching Dapper raw SQL extension methods on `DbContext` in `DbContextQueryExtensions` so raw SQL can also be executed directly on write contexts when needed.
2. **Clean Up Unused Read-Side Query / Specification Interfaces (Keep Only Write-Side Abstractions)**:
   * **Retain for Write Side (`DbContext`)**:
     * `IDbQuery<in TContext, TResult> where TContext : DbContext` and `db.QueryAsync(IDbQuery<TContext, TResult>, ct)`.
     * `ISpecification<TEntity>`, `Specification<TEntity>`, `SpecificationEvaluator`, `TenantByIdSpec`, `TenantBySlugSpec`, and `DbContext` specification extensions (`QueryFirstOrDefaultAsync<TEntity>`, `QueryListAsync<TEntity>`, `QueryAnyAsync<TEntity>`, `QueryCountAsync<TEntity>`).
   * **Remove Read-Side / Unused Abstractions**:
     * Remove `ISpecification<TEntity, TResult>` and `Specification<TEntity, TResult>` (projection specifications).
     * Remove `SpecificationEvaluator.GetQuery<TEntity, TResult>`.
     * Remove projection overloads (`QueryFirstOrDefaultAsync<TEntity, TResult>`, `QueryListAsync<TEntity, TResult>`) and all `ReadOnlyCatalogDbContext` / `ReadOnlyAppDbContext` `ISpecification` overloads from `DbContextQueryExtensions`.
3. **Refactor Read-Side Query Handlers to Dapper Raw SQL**:
   * Refactor `ListTenantsHandler` (removing `ListTenantsDbQuery`), `GetTenantByIdHandler`, `CheckTenantSlugHandler`, and `GetTenantProvisioningStatusHandler` to inject `ReadOnlyCatalogDbContext` and execute raw SQL directly via Dapper.
4. **Architecture & Integration Verification**:
   * Enforce via `ArchitectureTests` that `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` never expose `IQueryable` or accept `ISpecification`, and that `Domain` remains completely free of `Dapper` and `EF Core` references.

---

## 2. Dapper Raw SQL Execution Infrastructure (`Infrastructure/Persistence/`)

### 2.1. Package Dependency
* Add `<PackageVersion Include="Dapper" Version="2.1.66" />` to `apps/backend/Directory.Packages.props`.
* Add `<PackageReference Include="Dapper" />` to `apps/backend/src/ExamPrism.csproj`.

### 2.2. `DapperTypeHandlers` (`Infrastructure/Persistence/DapperTypeHandlers.cs`)
Because EF Core stores `Tenant.Status` as a `varchar(32)` string (`HasConversion<string>()`), Dapper needs a type handler to seamlessly map string columns and parameters to `TenantStatus` (including positional record constructors like `TenantSummaryItem`, `TenantDetailResult`, and `GetTenantProvisioningStatusResult`):

```csharp
namespace ExamPrism.Infrastructure.Persistence;

using System;
using System.Data;
using Common.MultiTenancy;
using Dapper;

public sealed class StringEnumTypeHandler<TEnum> : SqlMapper.TypeHandler<TEnum>
    where TEnum : struct, Enum
{
    public override void SetValue(IDbDataParameter parameter, TEnum value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value.ToString();
    }

    public override TEnum Parse(object value) => value switch
    {
        TEnum enumVal => enumVal,
        string str when Enum.TryParse<TEnum>(str, ignoreCase: true, out var parsed) => parsed,
        int intVal => (TEnum)Enum.ToObject(typeof(TEnum), intVal),
        _ => throw new DataException($"Cannot convert value '{value}' ({value?.GetType().FullName}) to {typeof(TEnum).Name}.")
    };
}

public static class DapperTypeHandlers
{
    private static int _registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        SqlMapper.AddTypeHandler(new StringEnumTypeHandler<TenantStatus>());
    }
}
```
`DapperTypeHandlers.EnsureRegistered()` is invoked during `PersistenceSetup.AddPersistence` and inside `DapperCommandExecutor` static initialization so both full DI hosts and standalone test `DbContext` instances have type handlers registered automatically.

### 2.3. `DapperCommandExecutor` (`Infrastructure/Persistence/DapperCommandExecutor.cs`)
Centralized internal executor that runs Dapper commands over a `DbContext`'s underlying `DbConnection` without disposing the connection, while propagating transactions, timeouts, cancellation tokens, and slow-query telemetry:

```csharp
namespace ExamPrism.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

internal static class DapperCommandExecutor
{
    static DapperCommandExecutor()
    {
        DapperTypeHandlers.EnsureRegistered();
    }

    public static async Task<IReadOnlyList<T>> QueryAsync<T>(
        DbContext db,
        string sql,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            var rows = await connection.QueryAsync<T>(command);
            return rows.AsList().AsReadOnly();
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    public static async Task<T?> QueryFirstOrDefaultAsync<T>(
        DbContext db,
        string sql,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await connection.QueryFirstOrDefaultAsync<T>(command);
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    public static async Task<T?> QuerySingleOrDefaultAsync<T>(
        DbContext db,
        string sql,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await connection.QuerySingleOrDefaultAsync<T>(command);
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    public static async Task<T> QuerySingleAsync<T>(
        DbContext db,
        string sql,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await connection.QuerySingleAsync<T>(command);
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    public static async Task<T?> ExecuteScalarAsync<T>(
        DbContext db,
        string sql,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await connection.ExecuteScalarAsync<T>(command);
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    public static async Task<TResult> QueryMultipleAsync<TResult>(
        DbContext db,
        string sql,
        Func<SqlMapper.GridReader, Task<TResult>> map,
        object? param = null,
        SlowQueryTelemetryInterceptor? interceptor = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        var (connection, command) = PrepareCommand(db, sql, param, ct);
        var start = Stopwatch.GetTimestamp();
        try
        {
            await using var grid = await connection.QueryMultipleAsync(command);
            return await map(grid);
        }
        finally
        {
            RecordTelemetry(db, interceptor, sql, Stopwatch.GetElapsedTime(start));
        }
    }

    private static (IDbConnection Connection, CommandDefinition Command) PrepareCommand(
        DbContext db,
        string sql,
        object? param,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);

        var connection = db.Database.GetDbConnection();
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var commandTimeout = db.Database.GetCommandTimeout();

        var command = new CommandDefinition(
            commandText: sql,
            parameters: param,
            transaction: transaction,
            commandTimeout: commandTimeout,
            commandType: CommandType.Text,
            flags: CommandFlags.Buffered,
            cancellationToken: ct);

        return (connection, command);
    }

    private static void RecordTelemetry(
        DbContext db,
        SlowQueryTelemetryInterceptor? interceptor,
        string sql,
        TimeSpan elapsed)
    {
        interceptor ??= db.GetService<IDbContextOptions>()
            ?.Extensions
            .OfType<CoreOptionsExtension>()
            .FirstOrDefault()
            ?.Interceptors
            ?.OfType<SlowQueryTelemetryInterceptor>()
            .FirstOrDefault();

        interceptor?.RecordIfSlow(sql, elapsed);
    }
}
```

### 2.4. `ReadOnlyCatalogDbContext` & `ReadOnlyAppDbContext`
Both read-only contexts wrap their underlying `CatalogDbContext` / `AppDbContext` (retaining `DatabaseContext`, `Context`, `Inner`, and `Dispose` / `DisposeAsync`), remove all `IQueryable<T>` and `ChangeTracker` properties, and expose the 6 Dapper raw SQL methods directly:
* `QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
* `QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
* `QuerySingleOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
* `QuerySingleAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
* `ExecuteScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default)`
* `QueryMultipleAsync<TResult>(string sql, Func<SqlMapper.GridReader, Task<TResult>> map, object? param = null, CancellationToken ct = default)`

In `PersistenceSetup.AddPersistence`, `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` pass the resolved `SlowQueryTelemetryInterceptor` instance into their constructors.

---

## 3. Query & Specification Cleanup

### 3.1. Files and Types Removed
* In `Common/Domain/Specifications/ISpecification.cs`:
  * Remove `ISpecification<TEntity, TResult>`.
* In `Common/Domain/Specifications/Specification.cs`:
  * Remove `Specification<TEntity, TResult>`.
* In `Infrastructure/Persistence/SpecificationEvaluator.cs`:
  * Remove `GetQuery<TEntity, TResult>(IQueryable<TEntity> inputQuery, ISpecification<TEntity, TResult> specification)`.

### 3.2. `IDbQuery<in TContext, TResult>` (`Common/Persistence/IDbQuery.cs`)
Remains in `Common/Persistence/IDbQuery.cs` for Write-side query objects. In `DbContextQueryExtensions`, `QueryAsync<TContext, TResult>(this TContext db, IDbQuery<TContext, TResult> query, CancellationToken ct = default)` adds `where TContext : DbContext` so `IDbQuery` is strictly bound to EF Core `DbContext` instances (`AppDbContext` / `CatalogDbContext`).

### 3.3. `DbContextQueryExtensions` (`Infrastructure/Persistence/DbContextQueryExtensions.cs`)
Contains only:
1. **Write-Side `IDbQuery` & `ISpecification<TEntity>` on `DbContext`**:
   * `QueryAsync<TContext, TResult>(this TContext db, IDbQuery<TContext, TResult> query, CancellationToken ct = default) where TContext : DbContext`
   * `QueryFirstOrDefaultAsync<TEntity>(this DbContext db, ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
   * `QueryListAsync<TEntity>(this DbContext db, ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
   * `QueryAnyAsync<TEntity>(this DbContext db, ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
   * `QueryCountAsync<TEntity>(this DbContext db, ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
2. **Dapper Raw SQL Extensions on `DbContext`**:
   * `QueryAsync<T>(this DbContext db, string sql, object? param = null, CancellationToken ct = default)`
   * `QueryFirstOrDefaultAsync<T>(this DbContext db, string sql, object? param = null, CancellationToken ct = default)`
   * `QuerySingleOrDefaultAsync<T>(this DbContext db, string sql, object? param = null, CancellationToken ct = default)`
   * `QuerySingleAsync<T>(this DbContext db, string sql, object? param = null, CancellationToken ct = default)`
   * `ExecuteScalarAsync<T>(this DbContext db, string sql, object? param = null, CancellationToken ct = default)`
   * `QueryMultipleAsync<TResult>(this DbContext db, string sql, Func<SqlMapper.GridReader, Task<TResult>> map, object? param = null, CancellationToken ct = default)`

All `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` `ISpecification` overloads and all `<TEntity, TResult>` projection overloads are removed.

---

## 4. Refactoring Read-Side Query Handlers to Raw SQL

### 4.1. `ListTenantsHandler` (`Features/Platform/Tenants/ListTenantsHandler.cs`)
* Remove `ListTenantsDbQuery`.
* Inject `ReadOnlyCatalogDbContext db`.
* Execute parameterized raw SQL via `db.QueryMultipleAsync`:
  ```csharp
  const string sql = """
      SELECT COUNT(*)
      FROM "Tenants"
      WHERE (@Status IS NULL OR "Status" = @Status)
        AND (@Pattern IS NULL OR "Slug" ILIKE @Pattern OR "Name" ILIKE @Pattern);

      SELECT "Id", "Slug", "Name", "Status", "CreatedAt", "ProvisionedAt", "UpdatedAt"
      FROM "Tenants"
      WHERE (@Status IS NULL OR "Status" = @Status)
        AND (@Pattern IS NULL OR "Slug" ILIKE @Pattern OR "Name" ILIKE @Pattern)
      ORDER BY "CreatedAt" DESC, "Slug" ASC
      LIMIT @Take OFFSET @Skip;
      """;
  ```
* Maps total count (`grid.ReadSingleAsync<int>()`) and page items (`(await grid.ReadAsync<TenantSummaryItem>()).AsList().AsReadOnly()`) in a single database round-trip.

### 4.2. `GetTenantByIdHandler` (`Features/Platform/Tenants/GetTenantByIdHandler.cs`)
* Inject `ReadOnlyCatalogDbContext db`.
* Query via `db.QueryFirstOrDefaultAsync<TenantDetailResult>`:
  ```csharp
  const string sql = """
      SELECT "Id", "Slug", "Name", "Status", "FailureReason", "CreatedAt", "ProvisionedAt", "UpdatedAt"
      FROM "Tenants"
      WHERE "Id" = @Id
      LIMIT 1;
      """;
  ```

### 4.3. `CheckTenantSlugHandler` (`Features/Tenants/CheckTenantSlugHandler.cs`)
* Inject `ReadOnlyCatalogDbContext db`.
* Check existence via `db.ExecuteScalarAsync<bool>`:
  ```csharp
  const string sql = """
      SELECT EXISTS (
          SELECT 1
          FROM "Tenants"
          WHERE "Slug" = @Slug
      );
      """;
  ```

### 4.4. `GetTenantProvisioningStatusHandler` (`Features/Tenants/GetTenantProvisioningStatusHandler.cs`)
* Inject `ReadOnlyCatalogDbContext db`.
* Query via `db.QueryFirstOrDefaultAsync<GetTenantProvisioningStatusResult>`:
  ```csharp
  const string sql = """
      SELECT "Slug", "Name", "Status", "ProvisionedAt"
      FROM "Tenants"
      WHERE "Slug" = @Slug
      LIMIT 1;
      """;
  ```

---

## 5. Architecture Enforcement & Testing Strategy

### 5.1. Architecture Tests (`ExamPrism.Tests/Architecture/ArchitectureTests.cs`)
1. Add `"Dapper"` to the forbidden namespace prefixes in `TypesInDomain_MustNotReferenceInfrastructureFeaturesEndpointsOrExternalFrameworks` so `Common.Domain` and `ExamPrism.Domain` remain 100% ORM/micro-ORM agnostic.
2. Add `ReadOnlyDbContexts_MustNotExposeIQueryableOrAcceptSpecifications`:
   * Verifies `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` declare no public properties or methods returning `IQueryable` / `IQueryable<>` or accepting `ISpecification<>`.

### 5.2. Unit & Integration Tests
1. **`DddPrimitivesTests.cs`**:
   * Remove `ProductDtoSpec` (`Specification<ProductItem, string>`) test assertions while keeping all `Specification<TEntity>` criteria and combinator tests.
2. **`DddPersistenceIntegrationTests.cs`**:
   * Verify Write-side `DbContext` executes `IDbQuery<CatalogDbContext, TResult>` and `ISpecification<Tenant>` (`QueryFirstOrDefaultAsync`, `QueryListAsync`, `QueryAnyAsync`, `QueryCountAsync`).
   * Verify `ReadOnlyCatalogDbContext` and `DbContext` execute Dapper raw SQL (`QueryAsync<T>`, `QueryFirstOrDefaultAsync<T>`, `QuerySingleOrDefaultAsync<T>`, `QuerySingleAsync<T>`, `ExecuteScalarAsync<T>`, `QueryMultipleAsync<TResult>`), including `TenantStatus` string-to-enum mapping and `SlowQueryTelemetryInterceptor` slow-query recording.
3. **`PersistenceHighLoadTests.cs`, `PlatformTenantHandlersTests.cs`, `TenantOnboardingHandlersTests.cs`**:
   * Update read-context assertions in `PersistenceHighLoadTests` (removing `.Tenants` / `.Users` `IQueryable` property checks).
   * Pass `new ReadOnlyCatalogDbContext(db)` into `ListTenantsHandler`, `GetTenantByIdHandler`, `CheckTenantSlugHandler`, and `GetTenantProvisioningStatusHandler` in handler integration tests.

