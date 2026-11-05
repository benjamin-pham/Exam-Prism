# Domain-Driven Design (DDD) Foundation & Dual-Tier Query Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish a complete Tactical Domain-Driven Design (DDD) foundation (`Entity<TId>`, `AggregateRoot<TId>`, `ValueObject`, `IDomainEvent`, `IAuditableEntity`), a dual-tier query engine (`ISpecification<T>`, `IDbQuery<TContext, TResult>`, `db.QueryAsync(...)`), EF Core lifecycle interceptors (`AuditableEntityInterceptor`, `DomainEventDispatchInterceptor`), the `ExamPrism.Domain.Tenants` rich aggregate bounded context, and strict DDD boundary checks in `ArchitectureTests`.

**Architecture:** Single-project modular Vertical Slice + Tactical DDD inside `apps/backend/src/ExamPrism.csproj`. `Common.Domain` holds base primitives and expression-tree specifications; `ExamPrism.Domain` holds pure business aggregates, value objects, domain events, and specifications; `Common.Persistence` holds `IDbQuery<in TContext, TResult>`; `ExamPrism.Infrastructure.Persistence` evaluates specifications/queries and dispatches domain events post-commit via Wolverine `IMessageBus`.

**Tech Stack:** .NET 10, C# 14, Entity Framework Core 10 (Npgsql), WolverineFx, FluentValidation, xUnit, FluentAssertions, NSubstitute, Testcontainers PostgreSQL.

**Spec:** `docs/superpowers/specs/2026-11-05-ddd-foundation-design.md`

## Global Constraints

- `Common.Domain` and `ExamPrism.Domain` MUST NOT reference `ExamPrism.Infrastructure`, `ExamPrism.Features`, `ExamPrism.Endpoints`, `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `Wolverine`, `Orleans`, or `StackExchange.Redis`.
- `Common.*` MUST NOT reference `ExamPrism.Domain.*`.
- `ExamPrism.Features.*` MUST NOT depend on any interface in `ExamPrism.Infrastructure.*` (`IDbQuery<in TContext, TResult>` MUST reside in `Common.Persistence`).
- Every concrete `IDomainEvent` implementation MUST be a `public sealed record` whose name ends with `DomainEvent`.
- Every concrete subclass of `ValueObject` MUST be `public sealed`.
- `Tenant` in `ExamPrism.Domain.Tenants` MUST retain compatibility with existing test data setup (`new Tenant { ... }` object initializers) while providing rich factory and state-transition methods returning `Result` / `Result<Tenant>`.
- EF Core Catalog migration snapshot (`CatalogDbContextModelSnapshot.cs`) and `.Designer.cs` files MUST update the `Tenant` entity type string to `"ExamPrism.Domain.Tenants.Tenant"` so `Database.MigrateAsync()` does not trigger `PendingModelChangesWarning`.

## Review Focus

1. **Transient Entity & Derived Type Equality (`Entity<TId>`):** Two newly instantiated entities with `default(TId)` (`Guid.Empty` or `0`) must NOT compare equal unless they are the same reference (`ReferenceEquals`), and two entities of different derived types sharing the same `Guid` ID must NOT compare equal.
2. **ValueObject Collection & Null Component Hashing (`ValueObject`):** A `ValueObject` whose `GetEqualityComponents()` yields `null` or sequence elements must not throw `NullReferenceException` in `Equals` or `GetHashCode`.
3. **Pagination TotalCount Corruption in `QueryCountAsync` / `QueryAnyAsync`:** Passing a paged/ordered `ISpecification<T>` (`Skip`/`Take`/`Includes`/`OrderBy`) into `db.QueryCountAsync(spec)` or `db.QueryAnyAsync(spec)` must ignore `Skip`, `Take`, `Includes`, and `OrderBy` so that total row counts are accurate and do not fail SQL translation.
4. **Uncommitted Domain Events Leaking on `SaveChangesAsync` Failure:** If `db.SaveChangesAsync()` throws a `DbUpdateException` (e.g., unique slug constraint violation), `DomainEventDispatchInterceptor` must clear the pending event list in `SaveChangesFailedAsync` and never publish uncommitted events on subsequent saves.
5. **EF Core 10 `PendingModelChangesWarning` on `Tenant` Namespace Move:** Moving `Tenant` from `ExamPrism.Infrastructure.Persistence.Catalog` to `ExamPrism.Domain.Tenants` changes the CLR type name in the EF Core model; `CatalogDbContextModelSnapshot.cs` and the Catalog migration `.Designer.cs` files must reflect `"ExamPrism.Domain.Tenants.Tenant"`, and `DomainEvents` must be ignored (`b.Ignore(t => t.DomainEvents)`) so `CatalogDbContextSchemaTests` and `TenantProvisionerTests` pass without model mismatch errors.

---

### Task 1: Tactical DDD Building Blocks & Expression Specifications (`Common/Domain`)

**Files:**
- Create: `apps/backend/src/Common/Domain/IDomainEvent.cs`
- Create: `apps/backend/src/Common/Domain/IAuditableEntity.cs`
- Create: `apps/backend/src/Common/Domain/Entity.cs`
- Create: `apps/backend/src/Common/Domain/AggregateRoot.cs`
- Create: `apps/backend/src/Common/Domain/ValueObject.cs`
- Create: `apps/backend/src/Common/Domain/Specifications/OrderExpression.cs`
- Create: `apps/backend/src/Common/Domain/Specifications/ISpecification.cs`
- Create: `apps/backend/src/Common/Domain/Specifications/ExpressionCombiner.cs`
- Create: `apps/backend/src/Common/Domain/Specifications/Specification.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Domain/DddPrimitivesTests.cs`

**Interfaces:**
- Consumes: `System.Linq.Expressions`
- Produces:
  - `Common.Domain.IDomainEvent` (`Guid EventId`, `DateTimeOffset OccurredOnUtc`)
  - `Common.Domain.DomainEventBase` (`protected DomainEventBase()`, `protected DomainEventBase(DateTimeOffset occurredOnUtc)`)
  - `Common.Domain.IAuditableEntity` (`DateTimeOffset CreatedAt`, `DateTimeOffset UpdatedAt`, `void SetCreated(DateTimeOffset now)`, `void SetUpdated(DateTimeOffset now)`)
  - `Common.Domain.Entity<TId> : IEquatable<Entity<TId>> where TId : notnull` (`public TId Id { get; set; }`, `public bool IsTransient()`)
  - `Common.Domain.IAggregateRoot` (`IReadOnlyList<IDomainEvent> DomainEvents`, `IReadOnlyList<IDomainEvent> DequeueDomainEvents()`, `void ClearDomainEvents()`)
  - `Common.Domain.AggregateRoot<TId> : Entity<TId>, IAggregateRoot where TId : notnull` (`protected void RaiseDomainEvent(IDomainEvent domainEvent)`)
  - `Common.Domain.ValueObject : IEquatable<ValueObject>` (`protected abstract IEnumerable<object?> GetEqualityComponents()`)
  - `Common.Domain.Specifications.OrderExpression<TEntity>(Expression<Func<TEntity, object?>> KeySelector, bool Descending = false)`
  - `Common.Domain.Specifications.ISpecification<TEntity>` & `ISpecification<TEntity, TResult> : ISpecification<TEntity>`
  - `Common.Domain.Specifications.Specification<TEntity> : ISpecification<TEntity>` (`Where`, `AddInclude`, `ApplyOrderBy`, `ApplyOrderByDescending`, `ApplyPaging`, `WithTracking`, `WithSplitQuery`, `WithIgnoreQueryFilters`, `IsSatisfiedBy`, `And`, `Or`, `Not`)
  - `Common.Domain.Specifications.Specification<TEntity, TResult> : Specification<TEntity>, ISpecification<TEntity, TResult>` (`protected void Select(Expression<Func<TEntity, TResult>> selector)`)

- [ ] **Step 1: Write the failing unit tests in `apps/backend/tests/ExamPrism.Tests/Domain/DddPrimitivesTests.cs`**

Test cases to include:
- `Entity_Equality_ComparesByIdAndExactType_AndTreatsTransientDefaultIdsAsDistinctUnlessSameReference` (pins Review Focus #1: two `SampleEntity(Guid.Empty)` are `!=`, same non-empty `Guid` on `SampleEntity` vs `OtherEntity` are `!=`, same non-empty `Guid` on two `SampleEntity` instances are `==` and have matching `GetHashCode()`).
- `ValueObject_Equality_ComparesAllComponents_HandlesNulls_AndRejectsDifferentDerivedTypes` (pins Review Focus #2: `Money(100, "USD") == Money(100, "USD")`, `Address("St", null) == Address("St", null)` without throwing in `GetHashCode()`).
- `AggregateRoot_RaiseAndDequeueDomainEvents_ReturnsSnapshotInFifoOrderAndClearsInternalQueue` (`RaiseDomainEvent` adds events, `DequeueDomainEvents()` returns all raised events in order and empties `DomainEvents`, `ClearDomainEvents()` empties queue).
- `Specification_IsSatisfiedBy_AndBooleanCombinators_AndOrNot_EvaluateCorrectlyInMemory` (`ActiveItemSpec.And(MinScoreSpec).Not()` evaluates `IsSatisfiedBy` accurately and configures `Includes`, `OrderExpressions`, `Skip`, `Take`, `AsNoTracking`, `AsSplitQuery`, `IgnoreQueryFilters`, and projection `Selector`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~DddPrimitivesTests"`
Expected: FAIL (compile error: `Common.Domain` and `Common.Domain.Specifications` types do not exist).

- [ ] **Step 3: Implement `Common/Domain` primitives and `Common/Domain/Specifications`**

Implement the 9 files under `apps/backend/src/Common/Domain/`:
- In `Entity<TId>`: `IsTransient()` returns `EqualityComparer<TId>.Default.Equals(Id, default!) || (Id is string s && string.IsNullOrWhiteSpace(s))`. `Equals` checks `other is not null && GetType() == other.GetType() && !IsTransient() && !other.IsTransient() && EqualityComparer<TId>.Default.Equals(Id, other.Id)`.
- In `ValueObject`: `GetHashCode()` combines `GetType()` and each component's hash code (`component?.GetHashCode() ?? 0`) via `HashCode.Combine`.
- In `ExpressionCombiner`: use a parameter-replacing `ExpressionVisitor` (`ReplaceParameterVisitor`) to rebind `right.Parameters[0]` to `left.Parameters[0]` for `Expression.AndAlso`, `Expression.OrElse`, and `Expression.Not`.
- In `Specification<TEntity>`: cache `Func<TEntity, bool>? _compiledCriteria` invalidated whenever `Where(...)` is called; `IsSatisfiedBy(TEntity entity)` evaluates `_compiledCriteria(entity)` (returning `true` when `Criteria is null`). Provide a concrete internal/private `CombinedSpecification<TEntity>` for `And`, `Or`, and `Not`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~DddPrimitivesTests"`
Expected: PASS (all tests in `DddPrimitivesTests` pass).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Domain apps/backend/tests/ExamPrism.Tests/Domain/DddPrimitivesTests.cs
git commit -m "feat(domain): add tactical DDD building blocks and expression specifications"
```

---

### Task 2: Dual-Tier Query Engine (`db.QueryAsync`) & EF Core Interceptors (`AuditableEntityInterceptor`, `DomainEventDispatchInterceptor`)

**Files:**
- Create: `apps/backend/src/Common/Persistence/IDbQuery.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/SpecificationEvaluator.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/DbContextQueryExtensions.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/Interceptors/AuditableEntityInterceptor.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/Interceptors/DomainEventDispatchInterceptor.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/PersistenceSetup.cs:20-62`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DddPersistenceIntegrationTests.cs`

**Interfaces:**
- Consumes: `Common.Domain.*`, `Common.Domain.Specifications.*`, `Wolverine.IMessageBus`
- Produces:
  - `Common.Persistence.IDbQuery<in TContext, TResult>` (`Task<TResult> ExecuteAsync(TContext db, CancellationToken ct = default)`)
  - `ExamPrism.Infrastructure.Persistence.SpecificationEvaluator`:
    - `public static IQueryable<TEntity> GetQuery<TEntity>(IQueryable<TEntity> inputQuery, ISpecification<TEntity> specification, bool evaluateCriteriaOnly = false) where TEntity : class`
    - `public static IQueryable<TResult> GetQuery<TEntity, TResult>(IQueryable<TEntity> inputQuery, ISpecification<TEntity, TResult> specification) where TEntity : class`
  - `ExamPrism.Infrastructure.Persistence.DbContextQueryExtensions`:
    - `QueryAsync<TContext, TResult>(this TContext db, IDbQuery<TContext, TResult> query, CancellationToken ct = default)`
    - `QueryFirstOrDefaultAsync<TEntity>`, `QueryFirstOrDefaultAsync<TEntity, TResult>`
    - `QueryListAsync<TEntity>`, `QueryListAsync<TEntity, TResult>`
    - `QueryAnyAsync<TEntity>`, `QueryCountAsync<TEntity>`
    (Overloaded for `DbContext`, `ReadOnlyAppDbContext`, and `ReadOnlyCatalogDbContext`)
  - `ExamPrism.Infrastructure.Persistence.Interceptors.AuditableEntityInterceptor : SaveChangesInterceptor`
  - `ExamPrism.Infrastructure.Persistence.Interceptors.DomainEventDispatchInterceptor : SaveChangesInterceptor`

- [ ] **Step 1: Write the failing integration tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/DddPersistenceIntegrationTests.cs`**

Test cases to include (using `CatalogDbFixture`):
- `DbContextQueryExtensions_ExecutesIDbQuery_AndSpecificationMethods_OnWriteAndReadOnlyContexts`:
  - Tests `db.QueryAsync(new CustomTenantCountQuery(...))`, `db.QueryFirstOrDefaultAsync(spec)`, `db.QueryFirstOrDefaultAsync(projectionSpec)`, `db.QueryListAsync(spec)`, `db.QueryListAsync(projectionSpec)`, `db.QueryAnyAsync(spec)`, and `db.QueryCountAsync(pagedSpec)` on both `CatalogDbContext` and `ReadOnlyCatalogDbContext`.
  - Pins Review Focus #3: `pagedSpec` with `ApplyPaging(skip: 0, take: 1)` on a 3-matching-row dataset returns `1` item from `QueryListAsync(pagedSpec)` and `3` from `QueryCountAsync(pagedSpec)`.
- `AuditableEntityInterceptor_AutomaticallyStampsCreatedAtAndUpdatedAt_WhenNotExplicitlySet`:
  - Verifies adding an `IAuditableEntity` with default timestamps populates `CreatedAt` and `UpdatedAt` from `FakeTimeProvider`, and modifying it updates `UpdatedAt` while preserving `CreatedAt`.
- `DomainEventDispatchInterceptor_PublishesEventsAfterSaveChanges_AndDiscardsEventsOnSaveFailure`:
  - Pins Review Focus #4: When an `IAggregateRoot` raises a domain event and `SaveChangesAsync()` succeeds, `IMessageBus.InvokeAsync` is called with the event after commit. When `SaveChangesAsync()` fails with `DbUpdateException` (duplicate unique key), `IMessageBus.InvokeAsync` is NOT called, and a subsequent valid `SaveChangesAsync()` on the same context does NOT replay the discarded event from the failed save.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~DddPersistenceIntegrationTests"`
Expected: FAIL (compile error: `IDbQuery`, `DbContextQueryExtensions`, `AuditableEntityInterceptor`, and `DomainEventDispatchInterceptor` do not exist).

- [ ] **Step 3: Implement `IDbQuery`, `SpecificationEvaluator`, `DbContextQueryExtensions`, and EF Interceptors**

1. Create `apps/backend/src/Common/Persistence/IDbQuery.cs`.
2. Create `apps/backend/src/Infrastructure/Persistence/SpecificationEvaluator.cs`:
   - Tag query with `.TagWith(specification.GetType().Name)`.
   - When `evaluateCriteriaOnly == true` (used by `QueryCountAsync` and `QueryAnyAsync`), apply only `IgnoreQueryFilters()` and `Criteria` (`Where`).
   - Otherwise apply `AsNoTracking()` / `AsTracking()`, `IgnoreQueryFilters()`, `AsSplitQuery()`, `Criteria`, `Includes`, `IncludeStrings`, `OrderExpressions`, and `Skip`/`Take`.
   - In `GetQuery<TEntity, TResult>`, validate `specification.Selector is not null`, evaluate the base query (with `Criteria`, `OrderExpressions`, `Skip`, `Take`), and append `.Select(specification.Selector)`.
3. Create `apps/backend/src/Infrastructure/Persistence/DbContextQueryExtensions.cs` with overloads for `DbContext`, `ReadOnlyAppDbContext`, and `ReadOnlyCatalogDbContext`.
4. Create `AuditableEntityInterceptor` and `DomainEventDispatchInterceptor` in `apps/backend/src/Infrastructure/Persistence/Interceptors/`:
   - In `DomainEventDispatchInterceptor`: store dequeued events per `DbContext` in `ConditionalWeakTable<DbContext, List<IDomainEvent>>`. Dequeue in `SavingChanges`/`SavingChangesAsync`, publish via `IMessageBus.InvokeAsync(domainEvent, cancellationToken)` in `SavedChangesAsync` (resolving `IMessageBus` from `IServiceProvider` if registered), and clear in `SaveChangesFailed`/`SaveChangesFailedAsync`.
5. Register `AuditableEntityInterceptor` and `DomainEventDispatchInterceptor` in `PersistenceSetup.AddPersistence` (`apps/backend/src/Infrastructure/Persistence/PersistenceSetup.cs`) and add them to `CatalogDbContext` and `AppDbContext` options alongside `SlowQueryTelemetryInterceptor`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~DddPersistenceIntegrationTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Persistence apps/backend/src/Infrastructure/Persistence apps/backend/tests/ExamPrism.Tests/Infrastructure/DddPersistenceIntegrationTests.cs
git commit -m "feat(persistence): add db.QueryAsync specification engine and DDD EF interceptors"
```

---

### Task 3: Reference Bounded Context `Domain/Tenants/` (`Tenant` Rich Aggregate, `TenantSlug`, Specifications & Handler Refactoring)

**Files:**
- Create: `apps/backend/src/Domain/Tenants/TenantSlug.cs`
- Create: `apps/backend/src/Domain/Tenants/TenantErrors.cs`
- Create: `apps/backend/src/Domain/Tenants/Events/TenantDomainEvents.cs`
- Create: `apps/backend/src/Domain/Tenants/Tenant.cs`
- Create: `apps/backend/src/Domain/Tenants/Specifications/TenantByIdSpec.cs`
- Create: `apps/backend/src/Domain/Tenants/Specifications/TenantBySlugSpec.cs`
- Create: `apps/backend/src/Infrastructure/Persistence/Catalog/Configurations/TenantEntityTypeConfiguration.cs`
- Create: `apps/backend/src/GlobalUsings.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/GlobalUsings.cs`
- Delete: `apps/backend/src/Infrastructure/Persistence/Catalog/Tenant.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/Catalog/CatalogDbContext.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/Migrations/Catalog/CatalogDbContextModelSnapshot.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/Migrations/Catalog/20261102121932_InitialCatalog.Designer.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/Migrations/Catalog/20261103042736_AddPlatformIdentity.Designer.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/Migrations/Catalog/20261103063233_AddTenantProvisioningMetadata.Designer.cs`
- Modify: `apps/backend/src/Features/Tenants/CheckTenantSlugHandler.cs`
- Modify: `apps/backend/src/Features/Tenants/CreateTenantHandler.cs`
- Modify: `apps/backend/src/Features/Tenants/GetTenantProvisioningStatusHandler.cs`
- Modify: `apps/backend/src/Features/Platform/Tenants/GetTenantByIdHandler.cs`
- Modify: `apps/backend/src/Features/Platform/Tenants/ListTenantsHandler.cs`
- Modify: `apps/backend/src/Features/Platform/Tenants/UpdateTenantHandler.cs`
- Modify: `apps/backend/src/Features/Platform/Tenants/SuspendTenantHandler.cs`
- Modify: `apps/backend/src/Features/Platform/Tenants/ReactivateTenantHandler.cs`
- Modify: `apps/backend/src/Features/Platform/Tenants/RetryTenantProvisioningHandler.cs`
- Modify: `apps/backend/src/Infrastructure/MultiTenancy/TenantProvisioner.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Domain/Tenants/TenantDomainTests.cs`

**Interfaces:**
- Consumes: Task 1 (`AggregateRoot<Guid>`, `ValueObject`, `DomainEventBase`, `IAuditableEntity`, `Specification<T>`), Task 2 (`IDbQuery`, `DbContextQueryExtensions`)
- Produces:
  - `ExamPrism.Domain.Tenants.TenantSlug : ValueObject` (`public string Value { get; }`, `public static Result<TenantSlug> Create(string? rawSlug)`)
  - `ExamPrism.Domain.Tenants.TenantErrors` (`TenantNotFound`, `SlugAlreadyTaken`, `InvalidSlug`, `NameRequired`, `NameTooLong`, `InvalidStatusTransition`)
  - `ExamPrism.Domain.Tenants.Events.*` (`TenantCreatedDomainEvent`, `TenantRenamedDomainEvent`, `TenantSuspendedDomainEvent`, `TenantReactivatedDomainEvent`, `TenantProvisioningRetriedDomainEvent`, `TenantProvisionedDomainEvent`, `TenantProvisioningFailedDomainEvent`)
  - `ExamPrism.Domain.Tenants.Tenant : AggregateRoot<Guid>, IAuditableEntity` (`Create`, `Rename`, `Suspend`, `Reactivate`, `RetryProvisioning`, `SetEncryptedConnectionString`, `MarkProvisioned`, `MarkProvisioningFailed`)
  - `ExamPrism.Domain.Tenants.Specifications.TenantByIdSpec : Specification<Tenant>`
  - `ExamPrism.Domain.Tenants.Specifications.TenantBySlugSpec : Specification<Tenant>`
  - `ExamPrism.Features.Platform.Tenants.ListTenantsDbQuery : IDbQuery<CatalogDbContext, ListTenantsResult>`

- [ ] **Step 1: Write the failing unit tests in `apps/backend/tests/ExamPrism.Tests/Domain/Tenants/TenantDomainTests.cs`**

Test cases to include:
- `TenantSlug_Create_TrimsAndLowercasesValidSlug_AndReturnsValidationErrorForInvalidSlug`
- `Tenant_Create_ValidatesInvariants_InitializesProvisioningStatus_AndRaisesTenantCreatedDomainEvent`
- `Tenant_Rename_TrimsName_ValidatesLength_UpdatesTimestamp_AndRaisesTenantRenamedDomainEvent`
- `Tenant_Suspend_And_Reactivate_EnforceStateTransitionsAndConnectionStringInvariants_AndRaiseDomainEvents`
- `Tenant_RetryProvisioning_MarkProvisioned_And_MarkProvisioningFailed_EnforceInvariantsTruncateFailureReasonAndRaiseDomainEvents`
- `TenantSpecs_TenantByIdSpec_And_TenantBySlugSpec_EvaluateInMemoryCriteriaAndConfigureTracking`

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~TenantDomainTests"`
Expected: FAIL (compile error: `ExamPrism.Domain.Tenants` types do not exist).

- [ ] **Step 3: Implement `ExamPrism.Domain.Tenants`, `TenantEntityTypeConfiguration`, and refactor Tenant handlers**

1. Create `TenantSlug`, `TenantErrors`, `TenantDomainEvents`, `Tenant`, `TenantByIdSpec`, and `TenantBySlugSpec` under `apps/backend/src/Domain/Tenants/`.
   - In `Tenant`: inherit `AggregateRoot<Guid>, IAuditableEntity`. Keep property setters (`Slug`, `Name`, `Status`, etc.) accessible so existing object-initializer fixtures in tests continue to compile, while providing `Create`, `Rename`, `Suspend`, `Reactivate`, `RetryProvisioning`, `SetEncryptedConnectionString`, `MarkProvisioned`, and `MarkProvisioningFailed` that enforce all domain invariants, update timestamps, and call `RaiseDomainEvent(...)`.
2. Add `global using ExamPrism.Domain.Tenants;` in `apps/backend/src/GlobalUsings.cs` and `apps/backend/tests/ExamPrism.Tests/GlobalUsings.cs`, and remove `apps/backend/src/Infrastructure/Persistence/Catalog/Tenant.cs`.
3. Create `apps/backend/src/Infrastructure/Persistence/Catalog/Configurations/TenantEntityTypeConfiguration.cs` (including `builder.Ignore(t => t.DomainEvents);`) and apply it in `CatalogDbContext.OnModelCreating`.
4. Update `"ExamPrism.Infrastructure.Persistence.Catalog.Tenant"` to `"ExamPrism.Domain.Tenants.Tenant"` in `CatalogDbContextModelSnapshot.cs` and the 3 Catalog migration `.Designer.cs` files (pins Review Focus #5).
5. Refactor `CheckTenantSlugHandler`, `CreateTenantHandler`, `GetTenantProvisioningStatusHandler`, `GetTenantByIdHandler`, `ListTenantsHandler` (introducing `ListTenantsDbQuery : IDbQuery<CatalogDbContext, ListTenantsResult>`), `UpdateTenantHandler`, `SuspendTenantHandler`, `ReactivateTenantHandler`, `RetryTenantProvisioningHandler`, and `TenantProvisioner` to use `Tenant` domain methods, `TenantErrors`, `TenantByIdSpec`, `TenantBySlugSpec`, and `db.QueryAsync` / `db.QueryFirstOrDefaultAsync` / `db.QueryAnyAsync`.

- [ ] **Step 4: Run `TenantDomainTests`, `CatalogDbContextSchemaTests`, `TenantOnboardingHandlersTests`, and `PlatformTenantHandlersTests` to verify they pass**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~TenantDomainTests|FullyQualifiedName~CatalogDbContextSchemaTests|FullyQualifiedName~TenantOnboardingHandlersTests|FullyQualifiedName~PlatformTenantHandlersTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src apps/backend/tests/ExamPrism.Tests
git commit -m "feat(tenants): refactor Tenant into rich DDD AggregateRoot with Specifications and Query Objects"
```

---

### Task 4: DDD Architecture Enforcement Rules (`ArchitectureTests`)

**Files:**
- Modify: `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`

**Interfaces:**
- Consumes: `Common.Domain.*`, `ExamPrism.Domain.*`
- Produces: 5 new `[Fact]` architecture guard tests in `ExamPrism.Tests.Architecture.ArchitectureTests`:
  - `TypesInDomain_MustNotReferenceInfrastructureFeaturesEndpointsOrExternalFrameworks`
  - `TypesInCommon_MustNotReferenceExamPrismDomain`
  - `DomainEvents_MustBePublicSealedRecords_EndingWithDomainEvent`
  - `DomainEntitiesAndAggregates_MustInheritFromEntityOrAggregateRoot`
  - `ValueObjects_WhenConcrete_MustBePublicSealed`

- [ ] **Step 1: Add the 5 DDD architecture tests to `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`**

Implement the 5 `[Fact]` methods in `ArchitectureTests.cs`:
- `TypesInDomain_MustNotReferenceInfrastructureFeaturesEndpointsOrExternalFrameworks`: inspects all types in `Common.Domain*` and `ExamPrism.Domain*` and asserts neither base types, interfaces, fields, properties, constructors, nor methods reference `ExamPrism.Infrastructure`, `ExamPrism.Features`, `ExamPrism.Endpoints`, `Microsoft.EntityFrameworkCore`, `Microsoft.AspNetCore`, `Wolverine`, `Orleans`, or `StackExchange.Redis`.
- `TypesInCommon_MustNotReferenceExamPrismDomain`: inspects all types in `Common*` and asserts none reference `ExamPrism.Domain*`.
- `DomainEvents_MustBePublicSealedRecords_EndingWithDomainEvent`: verifies concrete `IDomainEvent` types are `public`, `sealed`, `IsRecordType(t)`, and end with `"DomainEvent"`.
- `DomainEntitiesAndAggregates_MustInheritFromEntityOrAggregateRoot`: verifies concrete non-static classes in `ExamPrism.Domain` (that are not `ValueObject` subclasses, `ISpecification<>` implementations, or `Exception` subclasses) inherit from `Entity<>` or `AggregateRoot<>`.
- `ValueObjects_WhenConcrete_MustBePublicSealed`: verifies all non-abstract subclasses of `ValueObject` are `public` and `sealed`.

- [ ] **Step 2: Run `ArchitectureTests` and full test suite to verify everything passes**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~ArchitectureTests"`
Expected: PASS (all architecture tests pass).

- [ ] **Step 3: Commit**

```bash
git add apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs
git commit -m "test(arch): enforce DDD domain purity and building block conventions"
```
