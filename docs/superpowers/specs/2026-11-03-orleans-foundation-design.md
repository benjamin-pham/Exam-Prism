# Design Specification: Microsoft Orleans Foundation (Co-Hosted Dual-Mode Silo & Multi-Tenant Grain Isolation)

**Date:** 2026-11-03  
**Status:** Draft (Pending User Spec Review)  
**Scope:** Architectural Subsystem — Orleans Foundation  

---

## 1. Context, Goals, and Non-Goals

ExamPrism backend currently operates on ASP.NET Core 10 Minimal APIs (`IEndpoint` auto-registration, Wolverine `IHandler` vertical slices, `Finbuckle.MultiTenant` v10 with physical Database-per-Tenant PostgreSQL isolation, `CatalogDbContext` master catalog, `HybridCacheService` with optional Redis L2, and fixed-window rate limiting).

To support high-concurrency, stateful online exam workflows (such as per-student live exam sessions, high-frequency answer auto-saving in memory, deterministic single-threaded submission locking, countdown timers/reminders for auto-submission, and real-time room proctoring), this specification establishes the **Microsoft Orleans Foundation** for ExamPrism.

### 1.1. Goals
1. **Co-Hosted Dual-Mode Orleans Silo (`net10.0`)**:
   * Run the Orleans Silo co-hosted inside the ASP.NET Core host (`Program.cs`) alongside Wolverine.
   * Automatically switch between **In-Memory / Localhost Mode** (when `ConnectionStrings:Redis` is empty/unconfigured) and **Distributed Redis Mode** (when `ConnectionStrings:Redis` is configured) for Clustering, Grain Storage, and Reminders — matching the exact runtime convention of `HybridCacheService` and `RateLimitingSetup`.
   * Support dynamic loopback port allocation (`SiloPort = 0`, `GatewayPort = 0`) so concurrent `WebApplicationFactory<Program>` instances in integration tests never collide on TCP ports.
2. **Multi-Tenant Grain Isolation Bridge**:
   * Enforce a canonical compound grain key format (`"{tenantId:D}:{entityKey}"`) via `TenantGrainKey` and `ITenantGrainFactory` (`Common.Orleans`).
   * Automatically propagate and verify tenant identity across grain calls using `TenantOutgoingGrainCallFilter` and `TenantIncomingGrainCallFilter`, hydrating Finbuckle's `IMultiTenantContextSetter` from `CatalogMultiTenantStore` before grain method execution and restoring prior context in `finally` (mirroring `TenantWolverineMiddleware`).
   * Provide a `TenantScopedGrain` base class with `ExecuteInTenantScopeAsync(...)` so Grain Timers and Orleans Reminders (which execute outside HTTP and client request contexts) can safely resolve tenant-scoped services (`ICacheService` / `HybridCacheService`, `ITenantContext`, and per-tenant `AppDbContext`).
3. **Clear Separation Between `HybridCache` and Grain State**:
   * Shared read-only tenant data (e.g., exam paper snapshots, question banks, rubrics) uses `ICacheService` (`HybridCacheService` L1 + L2 Redis with stampede protection) inside grains.
   * Mutable per-actor state uses Orleans `[PersistentState]` (`"Default"` store backed by Memory in local mode or Redis in distributed mode).
4. **Silo Health Check & Architectural Guardrails**:
   * Register `OrleansHealthCheck` (`"orleans"`) in `/health` to verify `ISiloStatusOracle.CurrentStatus == SiloStatus.Active`.
   * Extend `ArchitectureTests` to prevent `ExamPrism.Features` from injecting raw `Orleans.IGrainFactory` or `Orleans.IClusterClient` directly (enforcing `ITenantGrainFactory`) and to enforce conventions on `TenantScopedGrain` implementations.

### 1.2. Non-Goals
* Business domain tables and endpoints for Question Banks, Exams, Exam Attempts, or Grading (to be designed and implemented in subsequent vertical-slice specifications on top of this foundation).
* Separate Silo worker microservice deployment (the Silo runs co-hosted inside `ExamPrism` Web API host).

---

## 2. Packages & Configuration Schema

### 2.1. NuGet Packages (`Directory.Packages.props` & `ExamPrism.csproj`)
Add official .NET 10 Microsoft Orleans packages (`10.4.2`):
* `Microsoft.Orleans.Server` (`10.4.2`)
* `Microsoft.Orleans.Clustering.Redis` (`10.4.2`)
* `Microsoft.Orleans.Persistence.Redis` (`10.4.2`)
* `Microsoft.Orleans.Reminders.Redis` (`10.4.2`)

### 2.2. Configuration Schema (`appsettings.json` & `appsettings.Development.json`)

```json
{
  "Orleans": {
    "ClusterId": "examprism-cluster",
    "ServiceId": "examprism-service",
    "StateStoreName": "Default",
    "SiloPort": 11111,
    "GatewayPort": 30000
  }
}
```

### 2.3. `OrleansOptions` (`ExamPrism.Infrastructure.Orleans`)
* `public const string SectionName = "Orleans";`
* `public const string DefaultStateStoreName = "Default";`
* `ClusterId` (`string`, default `"examprism-cluster"`): Identifier for the Orleans cluster.
* `ServiceId` (`string`, default `"examprism-service"`): Stable service identifier used by storage and reminders.
* `StateStoreName` (`string`, default `"Default"`): Name of the grain storage provider registered for `[PersistentState("...", "Default")]`.
* `SiloPort` (`int`, default `11111`): TCP port for silo-to-silo communication. When set to `0`, `OrleansSetup` dynamically allocates an available ephemeral TCP port on `IPAddress.Loopback`.
* `GatewayPort` (`int`, default `30000`): TCP port for client-to-silo gateway communication. When set to `0`, `OrleansSetup` dynamically allocates an available ephemeral TCP port on `IPAddress.Loopback` (distinct from `SiloPort`).

---

## 3. Clean Abstractions (`Common.Orleans`)

Placed in `apps/backend/src/Common/Orleans/` so feature handlers in `ExamPrism.Features` can interact with tenant-scoped grains without referencing infrastructure internals.

### 3.1. `OrleansTenantConstants`
```csharp
namespace Common.Orleans;

public static class OrleansTenantConstants
{
    public const string TenantIdRequestContextKey = "ExamPrism.TenantId";
    public const string DefaultStateStore = "Default";
}
```

### 3.2. `ITenantScopedGrain`
```csharp
namespace Common.Orleans;

using global::Orleans;

public interface ITenantScopedGrain : IGrainWithStringKey
{
}
```
All tenant-isolated grain interfaces in ExamPrism must inherit from `ITenantScopedGrain`.

### 3.3. `TenantGrainKey`
Encapsulates the canonical compound primary key format `"{tenantId:D}:{entityKey}"`:
```csharp
namespace Common.Orleans;

public readonly record struct TenantGrainKey(Guid TenantId, string EntityKey)
{
    public static string Format(Guid tenantId, string entityKey);
    public static TenantGrainKey Parse(string compoundKey);
    public static bool TryParse(string? compoundKey, out TenantGrainKey result);
    public override string ToString() => Format(TenantId, EntityKey);
}
```
Validation rules:
* `tenantId` must not be `Guid.Empty` (throws `ArgumentException`).
* `entityKey` must not be null, empty, or whitespace (throws `ArgumentException`); trimmed `entityKey` may contain colons (e.g., `"exam-1:student-2"`), as parsing splits on the **first** `':'` separator where the prefix before `':'` is a valid non-empty 36-character GUID (`Guid.TryParseExact(prefix, "D", out var tenantId)`).

### 3.4. `ITenantGrainFactory`
```csharp
namespace Common.Orleans;

public interface ITenantGrainFactory
{
    TGrainInterface GetGrain<TGrainInterface>(string entityKey)
        where TGrainInterface : ITenantScopedGrain;

    TGrainInterface GetGrain<TGrainInterface>(Guid entityKey)
        where TGrainInterface : ITenantScopedGrain;

    TGrainInterface GetGrainForTenant<TGrainInterface>(Guid tenantId, string entityKey)
        where TGrainInterface : ITenantScopedGrain;

    TGrainInterface GetGrainForTenant<TGrainInterface>(Guid tenantId, Guid entityKey)
        where TGrainInterface : ITenantScopedGrain;
}
```

---

## 4. Infrastructure Implementation (`ExamPrism.Infrastructure.Orleans`)

### 4.1. `TenantGrainFactory : ITenantGrainFactory`
Registered as `Scoped` in DI:
* Injects `IGrainFactory` and `ITenantContext`.
* **`GetGrain<TGrainInterface>(string entityKey)`**:
  1. Checks `_tenantContext.HasTenant && _tenantContext.TenantId is { } tenantId && tenantId != Guid.Empty`. If false, throws `InvalidOperationException("An active tenant context is required to resolve a tenant-scoped grain.")`.
  2. Delegates to `GetGrainForTenant<TGrainInterface>(tenantId, entityKey)`.
* **`GetGrainForTenant<TGrainInterface>(Guid tenantId, string entityKey)`**:
  1. Formats `compoundKey = TenantGrainKey.Format(tenantId, entityKey)`.
  2. Sets `RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, tenantId.ToString("D"))`.
  3. Returns `_grainFactory.GetGrain<TGrainInterface>(compoundKey)`.

### 4.2. `TenantOutgoingGrainCallFilter : IOutgoingGrainCallFilter`
Propagates the ambient tenant ID into Orleans `RequestContext` when outgoing grain calls are made:
1. Inspects `RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey) as string`.
2. If not already set, checks `IMultiTenantContextAccessor<ExamPrismTenantInfo>.MultiTenantContext?.TenantInfo`:
   * If `tenantInfo is { Status: TenantStatus.Active }` and ` tenantInfo.TenantGuid != Guid.Empty`, sets `RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, tenantInfo.TenantGuid.ToString("D"))`.
3. Calls `await context.Invoke()`.

### 4.3. `TenantIncomingGrainCallFilter : IIncomingGrainCallFilter`
Enforces tenant isolation and hydrates Finbuckle's `IMultiTenantContextSetter` for every incoming call to an `ITenantScopedGrain`:
1. Checks if `context.Grain is ITenantScopedGrain`. If not (e.g., internal Orleans system target or management grain), calls `await context.Invoke()` directly.
2. Extracts the grain's primary key string via `context.Grain.GetPrimaryKeyString()` (or `context.TargetId.Key`).
3. Parses the key using `TenantGrainKey.TryParse(rawKey, out var grainKey)`. If parsing fails, throws `InvalidOperationException($"Invalid tenant grain key format '{rawKey}'. Expected '{{tenantId}}:{{entityKey}}'.")`.
4. Checks `RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey) as string`:
   * If a non-whitespace value is present in `RequestContext`, parses it as a `Guid` and verifies it matches `grainKey.TenantId`. If it does not match, throws `InvalidOperationException($"Cross-tenant grain call rejected: RequestContext tenant '{requestTenantId}' does not match grain key tenant '{grainKey.TenantId:D}'.")`.
   * If not present in `RequestContext` (e.g., grain-to-self call or reminder tick), sets `RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, grainKey.TenantId.ToString("D"))`.
5. Resolves `ExamPrismTenantInfo` using an async DI scope (`IServiceScopeFactory.CreateAsyncScope()`):
   * Fast path: if `accessor.MultiTenantContext?.TenantInfo` is already active and matches `grainKey.TenantId`, reuses it.
   * Otherwise, queries `IMultiTenantStore<ExamPrismTenantInfo>.GetAsync(grainKey.TenantId.ToString("D"))` (which reads from `HybridCache` L1/L2 via `CatalogMultiTenantStore`).
   * If `tenantInfo is null || tenantInfo.Status != TenantStatus.Active`: throws `InvalidOperationException($"Cannot execute grain call for inactive or unknown tenant '{grainKey.TenantId:D}'.")`.
6. Saves `var priorContext = accessor.MultiTenantContext;`, assigns `setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo);`, and executes `await context.Invoke()` inside a `try / finally` block that restores `setter.MultiTenantContext = priorContext!`.

### 4.4. `TenantScopedGrain` Abstract Base Class
Provides built-in key parsing and scoped execution helpers for all tenant grains:
```csharp
namespace ExamPrism.Infrastructure.Orleans;

using Common.Orleans;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using global::Orleans;
using global::Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;

public abstract class TenantScopedGrain : Grain, ITenantScopedGrain
{
    public TenantGrainKey TenantKey { get; private set; }
    protected Guid TenantId => TenantKey.TenantId;
    protected string EntityKey => TenantKey.EntityKey;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var rawKey = this.GetPrimaryKeyString();
        if (!TenantGrainKey.TryParse(rawKey, out var parsedKey))
        {
            throw new InvalidOperationException(
                $"Grain '{GetType().Name}' cannot activate with invalid tenant key '{rawKey}'. Expected '{{tenantId}}:{{entityKey}}'.");
        }

        TenantKey = parsedKey;
        return base.OnActivateAsync(cancellationToken);
    }

    protected async Task ExecuteInTenantScopeAsync(
        Func<IServiceProvider, Task> action,
        CancellationToken cancellationToken = default)
    {
        await ExecuteInTenantScopeAsync<object?>(async sp =>
        {
            await action(sp);
            return null;
        }, cancellationToken);
    }

    protected async Task<TResult> ExecuteInTenantScopeAsync<TResult>(
        Func<IServiceProvider, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var scope = ServiceProvider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var accessor = sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        var setter = sp.GetRequiredService<IMultiTenantContextSetter>();
        var store = sp.GetRequiredService<IMultiTenantStore<ExamPrismTenantInfo>>();

        var tenantIdString = TenantId.ToString("D");
        var currentTenant = accessor.MultiTenantContext?.TenantInfo;
        var priorContext = accessor.MultiTenantContext;
        var overridden = false;

        if (currentTenant is null ||
            !string.Equals(currentTenant.Id, tenantIdString, StringComparison.OrdinalIgnoreCase))
        {
            var tenantInfo = await store.GetAsync(tenantIdString);
            if (tenantInfo is null || tenantInfo.Status != Common.MultiTenancy.TenantStatus.Active)
            {
                throw new InvalidOperationException(
                    $"Cannot execute scoped grain operation for inactive or unknown tenant '{tenantIdString}'.");
            }

            setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo);
            overridden = true;
        }

        RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, tenantIdString);

        try
        {
            return await action(sp);
        }
        finally
        {
            if (overridden)
            {
                setter.MultiTenantContext = priorContext!;
            }
        }
    }
}
```
Why `ExecuteInTenantScopeAsync` is essential:
* **Grain Timers (`RegisterGrainTimer`)** execute callbacks directly on the grain scheduler without passing through `IIncomingGrainCallFilter`, meaning `AsyncLocal` in `IMultiTenantContextAccessor<ExamPrismTenantInfo>` is `null`.
* **Scoped Dependencies (`AppDbContext`, `UserManager<ApplicationUser>`)** must not be constructor-injected into a long-lived Grain instance. Calling `ExecuteInTenantScopeAsync(async sp => ...)` creates a fresh `AsyncServiceScope`, populates `IMultiTenantContextSetter`, ensures `ICacheService` (`HybridCacheService`) prefixes keys with `"t:{TenantId}:"`, and resolves `AppDbContext` against the tenant's physical PostgreSQL database.

### 4.5. Dual-Mode Co-Hosted Silo Setup (`OrleansSetup`)
Defined in `ExamPrism.Infrastructure.Orleans.OrleansSetup`:
* Extension method `public static IHostBuilder UseExamPrismOrleans(this IHostBuilder hostBuilder)` called in `Program.cs` via `builder.Host.UseExamPrismOrleans();`.
* Extension method `public static IServiceCollection AddExamPrismOrleans(this IServiceCollection services, IConfiguration configuration, IHealthChecksBuilder healthChecksBuilder)` called in `DependencyInjection.AddInfrastructure(...)`.
* **Configuration & Mode Selection**:
  1. Binds `OrleansOptions` from `configuration.GetSection(OrleansOptions.SectionName)`.
  2. Resolves `siloPort` and `gatewayPort`: if either is `<= 0`, binds a temporary `TcpListener(IPAddress.Loopback, 0)` to obtain an available ephemeral loopback port.
  3. Configures `ClusterOptions` (`ClusterId = options.ClusterId`, `ServiceId = options.ServiceId`).
  4. Registers `siloBuilder.AddIncomingGrainCallFilter<TenantIncomingGrainCallFilter>()` and `siloBuilder.AddOutgoingGrainCallFilter<TenantOutgoingGrainCallFilter>()`.
  5. Inspects `var redisConnectionString = configuration.GetConnectionString("Redis");`:
     * **In-Memory Mode (`string.IsNullOrWhiteSpace(redisConnectionString)`)**:
       * `siloBuilder.UseLocalhostClustering(siloPort: siloPort, gatewayPort: gatewayPort, primarySiloEndpoint: null, serviceId: options.ServiceId, clusterId: options.ClusterId)`
       * `siloBuilder.AddMemoryGrainStorage(options.StateStoreName)`
       * `siloBuilder.UseInMemoryReminderService()`
     * **Distributed Redis Mode (`!string.IsNullOrWhiteSpace(redisConnectionString)`)**:
       * Parses `var redisOptions = ConfigurationOptions.Parse(redisConnectionString); redisOptions.AbortOnConnectFail = false;`
       * `siloBuilder.ConfigureEndpoints(siloPort: siloPort, gatewayPort: gatewayPort, listenOnAnyHostAddress: true)`
       * `siloBuilder.UseRedisClustering(opt => opt.ConfigurationOptions = redisOptions)`
       * `siloBuilder.AddRedisGrainStorage(options.StateStoreName, opt => opt.ConfigurationOptions = redisOptions)`
       * `siloBuilder.UseRedisReminderService(opt => opt.ConfigurationOptions = redisOptions)`
* **DI Registrations (`AddExamPrismOrleans`)**:
  * `services.Configure<OrleansOptions>(configuration.GetSection(OrleansOptions.SectionName));`
  * `services.AddScoped<ITenantGrainFactory, TenantGrainFactory>();`
  * `services.AddSingleton<TenantIncomingGrainCallFilter>();`
  * `services.AddSingleton<TenantOutgoingGrainCallFilter>();`
  * `healthChecksBuilder.AddCheck<OrleansHealthCheck>("orleans");`

### 4.6. `OrleansHealthCheck : IHealthCheck`
Placed in `ExamPrism.Infrastructure.Orleans.OrleansHealthCheck`:
* Injects `ISiloStatusOracle`.
* Checks `var status = _siloStatusOracle.CurrentStatus;`:
  * When `status == SiloStatus.Active`: returns `Task.FromResult(HealthCheckResult.Healthy("Orleans silo is active."))`.
  * Otherwise: returns `Task.FromResult(HealthCheckResult.Unhealthy($"Orleans silo is not active (CurrentStatus: {status})."))`.

---

## 5. Project Layout & File Locations

```
apps/backend/
├── Directory.Packages.props                                    <-- Add Microsoft.Orleans.* 10.4.2 packages
├── src/
│   ├── ExamPrism.csproj                                        <-- Add Microsoft.Orleans.* package references
│   ├── Program.cs                                              <-- Add builder.Host.UseExamPrismOrleans()
│   ├── appsettings.json                                        <-- Add Orleans configuration section
│   ├── Common/
│   │   └── Orleans/
│   │       ├── OrleansTenantConstants.cs                       <-- RequestContext key & store constants
│   │       ├── ITenantScopedGrain.cs                           <-- Marker interface for tenant grains
│   │       ├── TenantGrainKey.cs                               <-- "{tenantId:D}:{entityKey}" parser/formatter
│   │       └── ITenantGrainFactory.cs                          <-- Tenant-safe grain factory abstraction
│   └── Infrastructure/
│       ├── DependencyInjection.cs                              <-- Call services.AddExamPrismOrleans(...)
│       └── Orleans/
│           ├── OrleansOptions.cs                               <-- Configuration options POCO
│           ├── OrleansSetup.cs                                 <-- Co-hosted Dual-Mode Silo & DI setup
│           ├── TenantGrainFactory.cs                           <-- ITenantGrainFactory implementation
│           ├── TenantOutgoingGrainCallFilter.cs                <-- Outgoing RequestContext propagation
│           ├── TenantIncomingGrainCallFilter.cs                <-- Incoming tenant validation & Finbuckle context hydration
│           ├── TenantScopedGrain.cs                            <-- Base grain class with ExecuteInTenantScopeAsync
│           ├── OrleansHealthCheck.cs                           <-- Silo health check ("orleans")
│           └── Diagnostics/
│               └── TenantProbeGrain.cs                         <-- ITenantProbeGrain + TenantProbeGrain for foundation & architecture verification
└── tests/
    └── ExamPrism.Tests/
        ├── Architecture/
        │   └── ArchitectureTests.cs                            <-- Add grain & ITenantGrainFactory architectural rules
        └── Infrastructure/
            ├── CustomWebApplicationFactory.cs                  <-- Set Orleans:SiloPort=0 and Orleans:GatewayPort=0
            └── Orleans/
                ├── OrleansFoundationUnitTests.cs               <-- Unit tests for Key, Factory, Filters, HealthCheck
                ├── OrleansMultiTenantIntegrationTests.cs       <-- In-Memory multi-tenant grain, HybridCache & AppDbContext isolation tests
                └── OrleansRedisIntegrationTests.cs             <-- Testcontainers.Redis clustering, persistence, reminder & health tests
```

---

## 6. Architectural Rules (`ArchitectureTests`)

Extend `ExamPrism.Tests.Architecture.ArchitectureTests` with two new rules:

1. **`TypesInFeatures_MustNotReferenceRawOrleansGrainFactoryOrClusterClient`**:
   * Scans all types in `ExamPrism.Features.*`.
   * Asserts no field, property, constructor parameter, or method signature references `Orleans.IGrainFactory` or `Orleans.IClusterClient` directly. Feature slices must use `Common.Orleans.ITenantGrainFactory` to guarantee tenant key scoping.
2. **`TenantScopedGrains_MustBePublicSealedEndWithGrainAndImplementITenantScopedGrain`**:
   * Scans all concrete classes in `AppAssembly` inheriting from `TenantScopedGrain`.
   * Asserts each class is `public`, `sealed`, has a name ending with `"Grain"`, and implements at least one interface (other than `ITenantScopedGrain` itself) that inherits from `ITenantScopedGrain`.

---

## 7. Testing & Verification Strategy

### 7.1. Ephemeral Ports in `CustomWebApplicationFactory`
Update `CustomWebApplicationFactory.ConfigureWebHost` in-memory settings to include:
* `["Orleans:SiloPort"] = "0"`
* `["Orleans:GatewayPort"] = "0"`
This guarantees all existing and new integration test suites spin up co-hosted Orleans silos without TCP port collisions.

### 7.2. Unit Tests (`OrleansFoundationUnitTests`)
1. **`TenantGrainKey` Tests**:
   * Formats valid `Guid` and `entityKey` as `"{tenantId:D}:{entityKey}"`.
   * Preserves colons inside `entityKey` (e.g., `"exam-10:user-20"`).
   * Rejects `Guid.Empty`, null/whitespace `entityKey`, missing colon, or non-GUID prefix in `Format` / `Parse` / `TryParse`.
2. **`TenantGrainFactory` Tests**:
   * Resolves grain with compound key and sets `RequestContext` when `ITenantContext` has an active tenant.
   * Throws `InvalidOperationException` when called without an active tenant context.
   * `GetGrainForTenant` works without ambient `ITenantContext` and rejects `Guid.Empty`.
3. **`TenantOutgoingGrainCallFilter` & `TenantIncomingGrainCallFilter` Tests**:
   * Outgoing filter populates `RequestContext` from active `IMultiTenantContextAccessor<ExamPrismTenantInfo>` when not already set.
   * Incoming filter bypasses non-`ITenantScopedGrain` targets.
   * Incoming filter rejects malformed grain keys and cross-tenant calls (`RequestContext` tenant mismatching grain key tenant).
   * Incoming filter rejects unknown, `Provisioning`, `Suspended`, or `Failed` tenants.
   * Incoming filter sets `IMultiTenantContextSetter.MultiTenantContext` before `context.Invoke()` and restores the prior context in `finally` even when `context.Invoke()` throws.
4. **`OrleansHealthCheck` Tests**:
   * Returns `HealthCheckResult.Healthy` when `ISiloStatusOracle.CurrentStatus == SiloStatus.Active`.
   * Returns `HealthCheckResult.Unhealthy` for `SiloStatus.Starting`, `SiloStatus.ShuttingDown`, `SiloStatus.Dead`, etc.

### 7.3. Multi-Tenant Integration Tests (`OrleansMultiTenantIntegrationTests`)
Using `CustomWebApplicationFactory` (with `tenant-a`, `tenant-b`, `suspended-org`, and `provisioning-org`) and a test-only `ITenantProbeGrain : ITenantScopedGrain`:
1. **In-Memory Grain State Isolation**:
   * Calling `ITenantProbeGrain` with the same `entityKey = "attempt-1"` under `tenant-a` and `tenant-b` activates two distinct grain instances (`"{tenantA}:attempt-1"` and `"{tenantB}:attempt-1"`) whose `[PersistentState("probe", "Default")]` values never collide.
2. **Grain + `HybridCacheService` Isolation**:
   * Inside `TenantProbeGrain`, calling `ICacheService.GetOrCreateAsync("exam:1:snapshot", ...)` scopes cache entries under `"t:{tenantA}:exam:1:snapshot"` and `"t:{tenantB}:exam:1:snapshot"` without cross-tenant leakage.
3. **Timer / Reminder Callback Scope (`ExecuteInTenantScopeAsync`)**:
   * Clearing `RequestContext` and Finbuckle `AsyncLocal` context inside a grain method (simulating a Grain Timer or Reminder tick) and invoking `ExecuteInTenantScopeAsync` accurately re-hydrates `ITenantContext`, `ICacheService` (`"t:{tenantId}:"` prefix), and resolves the physical `AppDbContext` (`examprism_tenant_a` vs. `examprism_tenant_b`).
4. **Inactive Tenant Rejection**:
   * Calling `GetGrainForTenant` for `suspended-org` or an unknown tenant `Guid` throws `InvalidOperationException` inside `TenantIncomingGrainCallFilter`.

### 7.4. Distributed Redis Integration Tests (`OrleansRedisIntegrationTests`)
Using `Testcontainers.PostgreSql` + `Testcontainers.Redis`:
1. Starts the application with `ConnectionStrings:Redis` pointing to the Redis container.
2. Verifies `/health` returns `200 OK` with `"catalog_db"`, `"redis"`, and `"orleans"` all reporting `"Healthy"`.
3. Invokes `ITenantProbeGrain` to persist `[PersistentState("probe", "Default")]` via `WriteStateAsync()`, verifies that grain state is stored in Redis and survives grain deactivation/reactivation, and verifies reminder registration (`RegisterOrUpdateReminder`) succeeds against the Redis reminder table.
