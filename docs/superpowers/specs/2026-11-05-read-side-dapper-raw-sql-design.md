# Design Specification: Read-Side Native Dapper Raw SQL via DbContext & Wrapper Cleanup

**Date:** 2026-11-05  
**Status:** Approved  
**Scope:** Architectural Subsystem — Native Dapper Raw SQL Execution via `db.Database.GetDbConnection()` on `AppDbContext` / `CatalogDbContext`, Complete Elimination of `ReadOnly*DbContext` Wrappers, and Tenant Read Handler Refactoring  

---

## 1. Context, Goals, and Scope

ExamPrism previously maintained `ReadOnlyCatalogDbContext` and `ReadOnlyAppDbContext` wrappers around EF Core `DbContext` instances to separate read-side queries. To eliminate unnecessary abstraction layers while leveraging native Dapper performance, connection pooling, and multi-tenant resolution from EF Core:

1. **Native Dapper Raw SQL on `DbContext` Connection**:
   * Handlers obtain the underlying `DbConnection` directly via `var connection = db.Database.GetDbConnection()`.
   * Queries use Dapper's native extension methods (`connection.QueryAsync`, `connection.QueryFirstOrDefaultAsync`, `connection.QueryMultipleAsync`, `connection.ExecuteScalarAsync`) using `CommandDefinition` for parameter binding and `CancellationToken` propagation.
   * `AppDbContext` (for tenant-scoped operations) and `CatalogDbContext` (for platform/catalog operations) are injected directly into read-side feature handlers.
2. **Complete Removal of Read-Only Wrapper Layer**:
   * Delete `ReadOnlyCatalogDbContext.cs` and `ReadOnlyAppDbContext.cs`.
   * Delete `DapperCommandExecutor.cs` and all Dapper forwarder extension methods on `DbContext`.
   * Remove read-only context registrations from `PersistenceSetup.cs`.
3. **Write-Side Specification & Query Object Preservation**:
   * Retain `ISpecification<TEntity>`, `Specification<TEntity>`, `SpecificationEvaluator`, `TenantByIdSpec`, `TenantBySlugSpec`, and `DbContext` specification extensions (`QueryFirstOrDefaultAsync<TEntity>`, `QueryListAsync<TEntity>`, `QueryAnyAsync<TEntity>`, `QueryCountAsync<TEntity>`) strictly on `DbContext` for write-side commands and aggregate loading.
   * Retain `IDbQuery<in TContext, TResult> where TContext : DbContext` on `DbContext`.
4. **Refactor Read-Side Query Handlers to Native Dapper**:
   * Refactor `ListTenantsHandler`, `GetTenantByIdHandler`, `CheckTenantSlugHandler`, and `GetTenantProvisioningStatusHandler` to inject `CatalogDbContext` directly and execute native Dapper raw SQL.
5. **Dapper Enum Type Handling**:
   * Retain `StringEnumTypeHandler<TEnum>` and `DapperTypeHandlers.EnsureRegistered()` in `PersistenceSetup.AddPersistence` to map `TenantStatus` varchar values to enum.

---

## 2. Native Dapper Execution & Type Mapping

### 2.1. Native Dapper via `db.Database.GetDbConnection()`
Handlers execute native Dapper extension methods on the connection provided by EF Core:
```csharp
var connection = db.Database.GetDbConnection();
var result = await connection.QueryFirstOrDefaultAsync<TResult>(
    new CommandDefinition(sql, param, cancellationToken: ct));
```
EF Core manages the connection lifecycle; handlers must not dispose `connection`.

### 2.2. `DapperTypeHandlers` (`Infrastructure/Persistence/DapperTypeHandlers.cs`)
Maps string database columns (such as `TenantStatus` stored as `varchar(32)`) to C# enums for positional record DTOs:
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
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        SqlMapper.AddTypeHandler(new StringEnumTypeHandler<TenantStatus>());
    }
}
```
Invoked in `PersistenceSetup.AddPersistence`.

---

## 3. Query Extensions & Persistence Cleanup

### 3.1. Files Removed
* `apps/backend/src/Infrastructure/Persistence/ReadOnlyCatalogDbContext.cs`
* `apps/backend/src/Infrastructure/Persistence/ReadOnlyAppDbContext.cs`
* `apps/backend/src/Infrastructure/Persistence/DapperCommandExecutor.cs`

### 3.2. `DbContextQueryExtensions` (`Infrastructure/Persistence/DbContextQueryExtensions.cs`)
Retains only the write-side query/specification overloads on `DbContext`:
* `QueryAsync<TContext, TResult>(this TContext db, IDbQuery<TContext, TResult> query, CancellationToken ct = default) where TContext : DbContext`
* `QueryFirstOrDefaultAsync<TEntity>(this DbContext db, ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
* `QueryListAsync<TEntity>(this DbContext db, ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
* `QueryAnyAsync<TEntity>(this DbContext db, ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
* `QueryCountAsync<TEntity>(this DbContext db, ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`

All custom Dapper wrapper extensions on `DbContext` are removed in favor of native Dapper methods on `IDbConnection`.

---

## 4. Tenant Read Handlers Refactored to Native Dapper

### 4.1. `ListTenantsHandler` (`Features/Platform/Tenants/ListTenantsHandler.cs`)
* Injects `CatalogDbContext db`.
* Calls `var connection = db.Database.GetDbConnection();`
* Calls `await using var grid = await connection.QueryMultipleAsync(new CommandDefinition(sql, param, cancellationToken: ct));`
* Reads total count and paged items from grid.

### 4.2. `GetTenantByIdHandler` (`Features/Platform/Tenants/GetTenantByIdHandler.cs`)
* Injects `CatalogDbContext db`.
* Calls `connection.QueryFirstOrDefaultAsync<TenantDetailResult>(new CommandDefinition(sql, new { query.Id }, cancellationToken: ct))`.

### 4.3. `CheckTenantSlugHandler` (`Features/Tenants/CheckTenantSlugHandler.cs`)
* Injects `CatalogDbContext db`.
* Calls `connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { Slug = slug }, cancellationToken: ct))`.

### 4.4. `GetTenantProvisioningStatusHandler` (`Features/Tenants/GetTenantProvisioningStatusHandler.cs`)
* Injects `CatalogDbContext db`.
* Calls `connection.QueryFirstOrDefaultAsync<GetTenantProvisioningStatusResult>(new CommandDefinition(sql, new { Slug = slug }, cancellationToken: ct))`.
