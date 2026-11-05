# Design Specification: Domain-Driven Design (DDD) Foundation & Dual-Tier Query Engine

**Date:** 2026-11-05  
**Status:** Draft (Pending User Spec Review)  
**Scope:** Architectural Subsystem — Full Tactical DDD Setup, Query Object / Specification Engine (`db.QueryAsync`), EF Core Lifecycle Interceptors, and `Domain/Tenants` Rich Aggregate Refactoring  

---

## 1. Context, Goals, and Scope

ExamPrism is currently organized as a single-project Vertical Slice backend (`apps/backend/src/ExamPrism.csproj`) with `Endpoints`, `Features` (Wolverine handlers), `Infrastructure`, and `Common`. While `ArchitectureTests` enforces strict handler, endpoint, and layer rules, business entities (such as `Tenant`) are currently anemic models residing inside `ExamPrism.Infrastructure`, and LINQ queries are written inline across handlers.

This specification establishes a complete, production-grade **Domain-Driven Design (DDD)** architecture combined with a **Dual-Tier Query Engine (`db.QueryAsync`)** inside `ExamPrism.csproj`:

1. **Tactical DDD Primitives (`Common/Domain/`)**:
   * `IDomainEvent` and `DomainEventBase` record base type.
   * `IAuditableEntity` contract (`CreatedAt`, `UpdatedAt`, `SetCreated`, `SetUpdated`).
   * `Entity<TId>` with DDD identity equality (`Equals`, `GetHashCode`, `==`, `!=`, transient reference check, unproxied runtime type check).
   * `IAggregateRoot` and `AggregateRoot<TId>` with internal domain event queue (`RaiseDomainEvent`, `DequeueDomainEvents`, `ClearDomainEvents`).
   * `ValueObject` with structural component-wise equality (`GetEqualityComponents`).
2. **Dual-Tier Query & Specification Engine (`db.QueryAsync`)**:
   * **Tier 1 — Pure Domain Specifications (`Common/Domain/Specifications/`)**: Expression-tree based `ISpecification<TEntity>`, `Specification<TEntity>`, `ISpecification<TEntity, TResult>`, and `Specification<TEntity, TResult>` supporting `Criteria`, `Includes`, `OrderBy`, `Skip`/`Take`, `AsNoTracking`, `AsSplitQuery`, in-memory `IsSatisfiedBy(TEntity)`, and boolean combinators (`And`, `Or`, `Not`) with zero EF Core dependency.
   * **Tier 2 — EF Core Query Objects (`Common/Persistence/IDbQuery.cs`)**: `IDbQuery<in TContext, TResult>` for encapsulating multi-table joins, projections, aggregations, or compiled queries.
   * **Unified `DbContext` Query Extensions (`Infrastructure/Persistence/DbContextQueryExtensions.cs`)**: `db.QueryAsync(...)`, `db.QueryFirstOrDefaultAsync(...)`, `db.QueryListAsync(...)`, `db.QueryAnyAsync(...)`, and `db.QueryCountAsync(...)` across `AppDbContext`, `ReadOnlyAppDbContext`, `CatalogDbContext`, and `ReadOnlyCatalogDbContext`, with automatic `.TagWith(queryType.Name)` telemetry tagging for `SlowQueryTelemetryInterceptor`.
3. **EF Core Lifecycle Interceptors (`Infrastructure/Persistence/Interceptors/`)**:
   * `AuditableEntityInterceptor`: Automatically populates `CreatedAt` and `UpdatedAt` on `IAuditableEntity` entries using `TimeProvider` during `SavingChanges` / `SavingChangesAsync`.
   * `DomainEventDispatchInterceptor`: Dequeues `IDomainEvent` instances from tracked `IAggregateRoot` entities prior to save and dispatches them via Wolverine's `IMessageBus.InvokeAsync` after `SavedChangesAsync` succeeds (clearing queued events on failure/rollback).
4. **Reference Bounded Context Refactoring (`Domain/Tenants/`)**:
   * Refactor `Tenant` into a rich `AggregateRoot<Guid>, IAuditableEntity` in `ExamPrism.Domain.Tenants` (preserving backward-compatible type aliasing/forwarding for existing callers and tests where needed).
   * Introduce `TenantSlug` (`ValueObject`), `TenantErrors` (centralized domain errors), tenant domain events (`TenantCreatedDomainEvent`, `TenantRenamedDomainEvent`, `TenantSuspendedDomainEvent`, `TenantReactivatedDomainEvent`, `TenantProvisioningRetriedDomainEvent`, `TenantProvisionedDomainEvent`, `TenantProvisioningFailedDomainEvent`), and tenant specifications/query objects (`TenantByIdSpec`, `TenantBySlugSpec`, `ListTenantsDbQuery`).
   * Refactor Tenant onboarding and platform tenant handlers (`CreateTenantHandler`, `CheckTenantSlugHandler`, `GetTenantProvisioningStatusHandler`, `GetTenantByIdHandler`, `ListTenantsHandler`, `UpdateTenantHandler`, `SuspendTenantHandler`, `ReactivateTenantHandler`, `RetryTenantProvisioningHandler`, and `TenantProvisioner`) to use rich domain methods and `db.QueryAsync(...)`.
5. **Architecture Tests (`ExamPrism.Tests/Architecture/ArchitectureTests.cs`)**:
   * Enforce strict purity of `Common.Domain` and `ExamPrism.Domain` (no references to `Infrastructure`, `Features`, `Endpoints`, `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `Wolverine`, `Orleans`, or `StackExchange.Redis`).
   * Enforce conventions on `IDomainEvent`, `Entity<TId>`, `AggregateRoot<TId>`, and `ValueObject`.

### 1.1. Non-Goals
* Splitting `ExamPrism.csproj` into multiple `.csproj` files (single-project modular structure enforced via `ArchitectureTests` is retained).
* Moving ASP.NET Core Identity framework types (`ApplicationUser`, `PlatformUser`, `IdentityRole<Guid>`) into `ExamPrism.Domain` (they remain in `ExamPrism.Infrastructure.Identity` to prevent coupling `Domain` to `Microsoft.AspNetCore.Identity.EntityFrameworkCore`).

---

## 2. Tactical DDD Building Blocks (`Common/Domain/`)

### 2.1. `IDomainEvent` and `DomainEventBase` (`Common/Domain/IDomainEvent.cs`)
```csharp
namespace Common.Domain;

public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredOnUtc { get; }
}

public abstract record DomainEventBase(Guid EventId, DateTimeOffset OccurredOnUtc) : IDomainEvent
{
    protected DomainEventBase()
        : this(Guid.NewGuid(), DateTimeOffset.UtcNow)
    {
    }

    protected DomainEventBase(DateTimeOffset occurredOnUtc)
        : this(Guid.NewGuid(), occurredOnUtc)
    {
    }
}
```

### 2.2. `IAuditableEntity` (`Common/Domain/IAuditableEntity.cs`)
```csharp
namespace Common.Domain;

public interface IAuditableEntity
{
    DateTimeOffset CreatedAt { get; }
    DateTimeOffset UpdatedAt { get; }
    void SetCreated(DateTimeOffset now);
    void SetUpdated(DateTimeOffset now);
}
```

### 2.3. `Entity<TId>` (`Common/Domain/Entity.cs`)
* `public TId Id { get; protected set; } = default!;` (with `protected set` or `init` so EF Core and subclasses can assign it).
* `protected Entity() { }` for ORM materialization and `protected Entity(TId id)` for domain construction.
* Identity Equality Rules:
  * Two entities are equal if and only if they are both non-null, share the same exact runtime type (`GetType() == other.GetType()`), are not transient (`!IsTransient()`), and `EqualityComparer<TId>.Default.Equals(Id, other.Id)`.
  * An entity is considered transient (`IsTransient()`) when `Equals(Id, default(TId))` (or `Id is string s && string.IsNullOrWhiteSpace(s)`). Two transient instances are equal only if `ReferenceEquals(this, other)`.

### 2.4. `IAggregateRoot` and `AggregateRoot<TId>` (`Common/Domain/AggregateRoot.cs`)
```csharp
namespace Common.Domain;

public interface IAggregateRoot
{
    IReadOnlyList<IDomainEvent> DomainEvents { get; }
    IReadOnlyList<IDomainEvent> DequeueDomainEvents();
    void ClearDomainEvents();
}

public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot
    where TId : notnull
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot() { }

    protected AggregateRoot(TId id) : base(id) { }

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    protected void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }

    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        if (_domainEvents.Count == 0)
        {
            return [];
        }

        var snapshot = _domainEvents.ToArray();
        _domainEvents.Clear();
        return snapshot;
    }

    public void ClearDomainEvents() => _domainEvents.Clear();
}
```

### 2.5. `ValueObject` (`Common/Domain/ValueObject.cs`)
* Abstract class implementing `IEquatable<ValueObject>`.
* Declares `protected abstract IEnumerable<object?> GetEqualityComponents();`.
* Overrides `Equals(object?)`, `Equals(ValueObject?)`, `GetHashCode()`, `operator ==`, and `operator !=` using `GetType() == other.GetType()` and `GetEqualityComponents().SequenceEqual(other.GetEqualityComponents())`.

---

## 3. Dual-Tier Query & Specification Engine (`db.QueryAsync`)

### 3.1. Tier 1 — Pure Domain Specifications (`Common/Domain/Specifications/`)
Located in `Common/Domain/Specifications/` (`namespace Common.Domain.Specifications`) with **no dependency on EF Core**:

* **`OrderExpression<TEntity>`**:
  ```csharp
  public sealed record OrderExpression<TEntity>(
      Expression<Func<TEntity, object?>> KeySelector,
      bool Descending = false);
  ```
* **`ISpecification<TEntity>`**:
  * `Expression<Func<TEntity, bool>>? Criteria { get; }`
  * `IReadOnlyList<Expression<Func<TEntity, object?>>> Includes { get; }`
  * `IReadOnlyList<string> IncludeStrings { get; }`
  * `IReadOnlyList<OrderExpression<TEntity>> OrderExpressions { get; }`
  * `int? Skip { get; }`
  * `int? Take { get; }`
  * `bool AsNoTracking { get; }`
  * `bool AsSplitQuery { get; }`
  * `bool IgnoreQueryFilters { get; }`
  * `bool IsSatisfiedBy(TEntity entity);`
* **`ISpecification<TEntity, TResult> : ISpecification<TEntity>`**:
  * `Expression<Func<TEntity, TResult>> Selector { get; }`
* **`Specification<TEntity>`**:
  * Base class with protected configuration helpers:
    * `Where(Expression<Func<TEntity, bool>> criteria)` (combines via logical `AndAlso` if called multiple times).
    * `AddInclude(Expression<Func<TEntity, object?>> includeExpression)` and `AddInclude(string includeString)`.
    * `ApplyOrderBy(Expression<Func<TEntity, object?>> keySelector)` and `ApplyOrderByDescending(Expression<Func<TEntity, object?>> keySelector)`.
    * `ApplyPaging(int skip, int take)`.
    * `WithTracking()` (sets `AsNoTracking = false`; defaults to `AsNoTracking = true`).
    * `WithSplitQuery()` (sets `AsSplitQuery = true`).
    * `WithIgnoreQueryFilters()` (sets `IgnoreQueryFilters = true`).
  * `IsSatisfiedBy(TEntity entity)` compiles and caches `Criteria` lazily (`Func<TEntity, bool>`) and evaluates `entity` in memory.
  * Boolean combinators (`And(Specification<TEntity> other)`, `Or(Specification<TEntity> other)`, `Not()`) using an `ExpressionVisitor` parameter rebinder (`ExpressionCombiner`).
* **`Specification<TEntity, TResult> : Specification<TEntity>, ISpecification<TEntity, TResult>`**:
  * Adds `protected void Select(Expression<Func<TEntity, TResult>> selector)` and exposes `Selector`.

### 3.2. Tier 2 — EF Query Objects (`Common/Persistence/IDbQuery.cs`)
Located in `Common/Persistence/IDbQuery.cs` (`namespace Common.Persistence`):
```csharp
namespace Common.Persistence;

public interface IDbQuery<in TContext, TResult>
{
    Task<TResult> ExecuteAsync(TContext db, CancellationToken ct = default);
}
```

### 3.3. Specification Evaluator & `db.QueryAsync` Extensions (`Infrastructure/Persistence/`)
1. **`SpecificationEvaluator` (`Infrastructure/Persistence/SpecificationEvaluator.cs`)**:
   * Translates `ISpecification<TEntity>` and `ISpecification<TEntity, TResult>` onto an `IQueryable<TEntity>`:
     * Automatically tags the query with `.TagWith(specification.GetType().Name)`.
     * Applies `AsNoTracking()` (when `specification.AsNoTracking` is `true`) or `AsTracking()` (when `false`).
     * Applies `IgnoreQueryFilters()` and `AsSplitQuery()` when enabled.
     * Applies `Criteria` (`Where`), `Includes`, `IncludeStrings`, `OrderExpressions` (`OrderBy`/`OrderByDescending` followed by `ThenBy`/`ThenByDescending`), and `Skip`/`Take`.
     * For `ISpecification<TEntity, TResult>`, applies `.Select(specification.Selector)` before or after paging appropriately (ordering/filtering before `Select`, `Skip`/`Take` applied cleanly).
2. **`DbContextQueryExtensions` (`Infrastructure/Persistence/DbContextQueryExtensions.cs`)**:
   * Overloads provided for `DbContext` (covering `AppDbContext` and `CatalogDbContext`), `ReadOnlyAppDbContext`, and `ReadOnlyCatalogDbContext`:
     * `Task<TResult> QueryAsync<TContext, TResult>(this TContext db, IDbQuery<TContext, TResult> query, CancellationToken ct = default)`
     * `Task<TEntity?> QueryFirstOrDefaultAsync<TEntity>(this ..., ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
     * `Task<TResult?> QueryFirstOrDefaultAsync<TEntity, TResult>(this ..., ISpecification<TEntity, TResult> specification, CancellationToken ct = default) where TEntity : class`
     * `Task<List<TEntity>> QueryListAsync<TEntity>(this ..., ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
     * `Task<List<TResult>> QueryListAsync<TEntity, TResult>(this ..., ISpecification<TEntity, TResult> specification, CancellationToken ct = default) where TEntity : class`
     * `Task<bool> QueryAnyAsync<TEntity>(this ..., ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
     * `Task<int> QueryCountAsync<TEntity>(this ..., ISpecification<TEntity> specification, CancellationToken ct = default) where TEntity : class`
   * Note on `QueryCountAsync` / `QueryAnyAsync`: Evaluates only `Criteria` and `IgnoreQueryFilters` (ignoring `Skip`, `Take`, `Includes`, and `OrderExpressions`) so total counts for pagination are accurate and fast.

---

## 4. EF Core Interceptors (`Infrastructure/Persistence/Interceptors/`)

### 4.1. `AuditableEntityInterceptor` (`Infrastructure/Persistence/Interceptors/AuditableEntityInterceptor.cs`)
* Registered as a Singleton in `PersistenceSetup.AddPersistence` and attached to `CatalogDbContext` and `AppDbContext`.
* Injects `TimeProvider`.
* Overrides `SavingChanges` and `SavingChangesAsync`:
  * Iterates `context.ChangeTracker.Entries<IAuditableEntity>()`:
    * If `entry.State == EntityState.Added`:
      * Calls `entry.Entity.SetCreated(now)` only when `CreatedAt == default`, and calls `entry.Entity.SetUpdated(now)` when `UpdatedAt == default` (preserving explicitly supplied timestamps from domain factory methods).
    * If `entry.State == EntityState.Modified`:
      * Calls `entry.Entity.SetUpdated(now)` if the `UpdatedAt` property was not already explicitly modified in the current unit of work.

### 4.2. `DomainEventDispatchInterceptor` (`Infrastructure/Persistence/Interceptors/DomainEventDispatchInterceptor.cs`)
* Registered in `PersistenceSetup.AddPersistence` and attached to `CatalogDbContext` and `AppDbContext`.
* Injects `IServiceProvider` (resolving `IMessageBus` on dispatch if registered in the current scope).
* Maintains a thread-safe `ConditionalWeakTable<DbContext, List<IDomainEvent>>` associating queued domain events with the specific `DbContext` instance being saved:
  * In `SavingChanges` / `SavingChangesAsync`:
    * Finds all tracked `IAggregateRoot` entries with `DomainEvents.Count > 0`.
    * Calls `DequeueDomainEvents()` on each aggregate and appends them to the `DbContext`'s pending list.
  * In `SavedChangesAsync`:
    * Removes the pending list for `eventData.Context`.
    * If events exist and `IMessageBus` is available, dispatches each `IDomainEvent` sequentially via `await bus.InvokeAsync(domainEvent, cancellationToken)`.
  * In `SaveChangesFailed` / `SaveChangesFailedAsync`:
    * Removes and discards the pending list for `eventData.Context` so uncommitted events are never published.

---

## 5. Reference Bounded Context: `Domain/Tenants/`

### 5.1. Value Object: `TenantSlug` (`Domain/Tenants/TenantSlug.cs`)
* `public sealed class TenantSlug : ValueObject`
* Property: `public string Value { get; }`
* `public static Result<TenantSlug> Create(string? rawSlug)`:
  * Normalizes `var normalized = (rawSlug ?? string.Empty).Trim().ToLowerInvariant();`.
  * Validates via `TenantSlugValidator.IsValid(normalized)`. If invalid, returns `TenantErrors.InvalidSlug()`.
  * Returns `new TenantSlug(normalized)`.
* Implicit conversion `public static implicit operator string(TenantSlug slug) => slug.Value;` and `ToString() => Value`.

### 5.2. Domain Errors: `TenantErrors` (`Domain/Tenants/TenantErrors.cs`)
Centralizes all tenant domain `Error` definitions with exact error codes expected by existing API contracts:
* `TenantNotFound()` → `Error.NotFound("TENANT_NOT_FOUND", "The requested organization was not found.")`
* `SlugAlreadyTaken()` → `Error.Conflict("SLUG_ALREADY_TAKEN", "This organization subdomain is already taken or reserved.")`
* `InvalidSlug()` → `Error.Validation("Slug", "INVALID_SLUG", "The organization subdomain slug is invalid.")`
* `NameRequired()` → `Error.Validation("Name", "REQUIRED", "Organization name is required.")`
* `NameTooLong()` → `Error.Validation("Name", "MAX_LENGTH", "Organization name must not exceed 200 characters.")`
* `InvalidStatusTransition(string message)` → `Error.Conflict("INVALID_TENANT_STATUS_TRANSITION", message)`

### 5.3. Domain Events (`Domain/Tenants/Events/TenantDomainEvents.cs`)
All `public sealed record` inheriting `DomainEventBase`:
* `TenantCreatedDomainEvent(Guid TenantId, string Slug, DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc)`
* `TenantRenamedDomainEvent(Guid TenantId, string OldName, string NewName, DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc)`
* `TenantSuspendedDomainEvent(Guid TenantId, string Slug, DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc)`
* `TenantReactivatedDomainEvent(Guid TenantId, string Slug, DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc)`
* `TenantProvisioningRetriedDomainEvent(Guid TenantId, string Slug, DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc)`
* `TenantProvisionedDomainEvent(Guid TenantId, string Slug, DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc)`
* `TenantProvisioningFailedDomainEvent(Guid TenantId, string Slug, string FailureReason, DateTimeOffset OccurredOnUtc) : DomainEventBase(OccurredOnUtc)`

### 5.4. Aggregate Root: `Tenant` (`Domain/Tenants/Tenant.cs`)
* `public class Tenant : AggregateRoot<Guid>, IAuditableEntity`
* To preserve 100% source compatibility with existing test fixtures and migrations that import `ExamPrism.Infrastructure.Persistence.Catalog`, `Tenant` is defined in `Domain/Tenants/Tenant.cs` (`namespace ExamPrism.Domain.Tenants`) and re-exported via `global using Tenant = ExamPrism.Domain.Tenants.Tenant;` or namespace alias where appropriate, while keeping public property getters/setters compatible with existing test setup data and adding rich domain behavior:
  * `public static Result<Tenant> Create(string slug, string name, string ownerEmail, string encryptedOwnerPassword, DateTimeOffset now)`
  * `public Result Rename(string newName, DateTimeOffset now)`
  * `public Result Suspend(DateTimeOffset now)`
  * `public Result Reactivate(DateTimeOffset now)`
  * `public Result RetryProvisioning(DateTimeOffset now)`
  * `public void SetEncryptedConnectionString(string encryptedConnectionString, DateTimeOffset now)`
  * `public void MarkProvisioned(string encryptedConnectionString, DateTimeOffset now)`
  * `public void MarkProvisioningFailed(string failureReason, DateTimeOffset now)`
* EF Core mapping for `Tenant` is extracted into `Infrastructure/Persistence/Catalog/Configurations/TenantEntityTypeConfiguration.cs` (`IEntityTypeConfiguration<Tenant>`) and ignored for `DomainEvents` (`builder.Ignore(t => t.DomainEvents)`).

### 5.5. Tenant Specifications & Query Objects
* `TenantByIdSpec : Specification<Tenant>` (`Domain/Tenants/Specifications/TenantByIdSpec.cs`):
  * Constructor `TenantByIdSpec(Guid id, bool track = false)` — filters `t => t.Id == id` and calls `WithTracking()` when `track == true`.
* `TenantBySlugSpec : Specification<Tenant>` (`Domain/Tenants/Specifications/TenantBySlugSpec.cs`):
  * Constructor `TenantBySlugSpec(string slug, bool track = false)` — filters `t => t.Slug == slug`.
* `ListTenantsDbQuery : IDbQuery<CatalogDbContext, PagedTenantsResult>` (`Features/Platform/Tenants/ListTenantsHandler.cs`):
  * Encapsulates the status filter, case-insensitive `Name`/`Slug` search, total count, `OrderByDescending(t => t.CreatedAt)`, and pagination projection into `PagedTenantsResult`.

---

## 6. Architecture Enforcement (`ArchitectureTests.cs`)

New architecture tests added to `ExamPrism.Tests/Architecture/ArchitectureTests.cs`:
1. `TypesInDomain_MustNotReferenceInfrastructureFeaturesEndpointsOrExternalFrameworks`:
   * Verifies every type in `Common.Domain*` and `ExamPrism.Domain*` has zero references (base types, interfaces, fields, properties, constructors, methods) to:
     * `ExamPrism.Infrastructure`, `ExamPrism.Features`, `ExamPrism.Endpoints`
     * `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `Wolverine`, `Orleans`, `StackExchange.Redis`
2. `TypesInCommon_MustNotReferenceExamPrismDomain`:
   * Ensures `Common.*` never depends upward on `ExamPrism.Domain.*`.
3. `DomainEvents_MustBePublicSealedRecords_EndingWithDomainEvent`:
   * Verifies every concrete type implementing `IDomainEvent` is a `public sealed` C# record whose name ends with `DomainEvent`.
4. `DomainEntitiesAndAggregates_MustInheritFromEntityOrAggregateRoot`:
   * Verifies concrete entity/aggregate classes in `ExamPrism.Domain` (excluding `ValueObject`, specifications, static error classes, and enums/records) inherit from `Entity<>` or `AggregateRoot<>`.
5. `ValueObjects_WhenConcrete_MustBePublicSealed`:
   * Verifies every concrete subclass of `ValueObject` is `public sealed`.

---

## 7. Testing Strategy

1. **DDD Primitives Unit Tests (`ExamPrism.Tests/Domain/DddPrimitivesTests.cs`)**:
   * `Entity<TId>` equality: same ID, different ID, transient default ID, derived type mismatch, `==`/`!=`, `GetHashCode`.
   * `ValueObject` structural equality: matching components, differing components, null components, `==`/`!=`, `GetHashCode`.
   * `AggregateRoot<TId>`: raising events, `DequeueDomainEvents` returns snapshot and clears queue, `ClearDomainEvents`.
   * `Specification<T>`: `IsSatisfiedBy` in-memory evaluation, `And`, `Or`, `Not` composition, paging/ordering/tracking metadata.
2. **Tenant Aggregate & Value Object Unit Tests (`ExamPrism.Tests/Domain/Tenants/TenantDomainTests.cs`)**:
   * `TenantSlug.Create` normalization and validation failure.
   * `Tenant.Create`, `Rename`, `Suspend`, `Reactivate`, `RetryProvisioning`, `MarkProvisioned`, `MarkProvisioningFailed` state transitions, error codes, and raised `IDomainEvent` instances.
3. **Persistence & Query Engine Integration Tests (`ExamPrism.Tests/Infrastructure/DddPersistenceIntegrationTests.cs`)**:
   * `db.QueryAsync`, `db.QueryFirstOrDefaultAsync` (entity & projection), `db.QueryListAsync` (entity & projection), `db.QueryAnyAsync`, `db.QueryCountAsync` against real PostgreSQL (`CatalogDbContext`, `ReadOnlyCatalogDbContext`, `AppDbContext`, `ReadOnlyAppDbContext`).
   * `AuditableEntityInterceptor` automatic `CreatedAt` / `UpdatedAt` stamping via `FakeTimeProvider`.
   * `DomainEventDispatchInterceptor` post-save Wolverine `IMessageBus` dispatch and rollback discard behavior.
4. **Full Regression Suite**:
   * All existing unit, architecture, and integration tests continue to pass without regression.
