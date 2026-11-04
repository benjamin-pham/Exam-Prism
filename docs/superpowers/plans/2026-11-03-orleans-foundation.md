# Microsoft Orleans Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish a co-hosted Microsoft Orleans 10.4.2 foundation in ExamPrism that automatically switches between In-Memory/Localhost mode and Distributed Redis mode based on `ConnectionStrings:Redis`, enforces strict per-tenant grain isolation via `ITenantGrainFactory`, `TenantGrainKey`, and grain call filters, bridges Finbuckle `MultiTenantContext` into grain methods and timer/reminder scopes for `HybridCacheService` and `AppDbContext`, and monitors silo health on `/health`.

**Architecture:** `Common/Orleans` exposes clean abstractions (`ITenantScopedGrain`, `TenantGrainKey`, `ITenantGrainFactory`, `OrleansTenantConstants`) so vertical slices in `ExamPrism.Features` never manipulate raw compound keys or depend on infrastructure internals. `ExamPrism.Infrastructure.Orleans` hosts the silo inside the ASP.NET Core process (`UseExamPrismOrleans`), propagates and validates tenant context across grain calls via `TenantOutgoingGrainCallFilter` and `TenantIncomingGrainCallFilter`, provides `TenantScopedGrain` with `ExecuteInTenantScopeAsync` for timer/reminder callbacks, and registers `OrleansHealthCheck` (`"orleans"`).

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core 10 Minimal APIs, `Microsoft.Orleans.Server` 10.4.2, `Microsoft.Orleans.Clustering.Redis` 10.4.2, `Microsoft.Orleans.Persistence.Redis` 10.4.2, `Microsoft.Orleans.Reminders.Redis` 10.4.2, `Finbuckle.MultiTenant` 10.1.5, `Microsoft.Extensions.Caching.Hybrid` 10.0.0, `StackExchange.Redis` 2.8.0, xUnit, FluentAssertions, NSubstitute, `Testcontainers.PostgreSql` 4.16.0, `Testcontainers.Redis` 4.16.0.

**Spec:** `docs/superpowers/specs/2026-11-03-orleans-foundation-design.md`

## Global Constraints

- Target framework `.NET 10` (`net10.0`), C# 13, Central Package Management in `apps/backend/Directory.Packages.props`.
- Exact NuGet package versions: `Microsoft.Orleans.Server` `10.4.2`, `Microsoft.Orleans.Clustering.Redis` `10.4.2`, `Microsoft.Orleans.Persistence.Redis` `10.4.2`, `Microsoft.Orleans.Reminders.Redis` `10.4.2`.
- Canonical compound grain key format is `"{tenantId:D}:{entityKey}"` where `tenantId` is a non-empty lowercase GUID (`Guid.Empty` is forbidden) and `entityKey` is non-whitespace.
- When `ConnectionStrings:Redis` is `null`, `""`, or whitespace, the silo must use `UseLocalhostClustering`, `AddMemoryGrainStorage`, and `UseInMemoryReminderService`.
- When `ConnectionStrings:Redis` is configured and reachable, the silo must use `UseRedisClustering`, `AddRedisGrainStorage`, and `UseRedisReminderService`.
- Preserve all existing `ArchitectureTests` and extend them so types in `ExamPrism.Features.*` never reference `Orleans.IGrainFactory` or `Orleans.IClusterClient` directly, and all concrete `TenantScopedGrain` subclasses are `public`, `sealed`, end with `"Grain"`, and implement an interface inheriting from `ITenantScopedGrain`.

## Review Focus

1. **Composite `EntityKey` values containing colons (e.g., `"exam-10:student-20"`)**: `TenantGrainKey.TryParse` must split only on the **first** `':'` character rather than calling `Split(':')` and requiring length 2, so sub-keys with colons are preserved verbatim (tested in Task 1).
2. **`AsyncLocal` tenant context leak when a grain method throws an exception**: `TenantIncomingGrainCallFilter.Invoke` and `TenantScopedGrain.ExecuteInTenantScopeAsync` must always restore `setter.MultiTenantContext = priorContext!` inside a `finally` block even when the inner call throws (tested in Task 2).
3. **Grain Timer / Reminder callbacks losing Finbuckle `AsyncLocal` context and writing to `"platform:"` in `HybridCacheService`**: `TenantScopedGrain.ExecuteInTenantScopeAsync` must hydrate `IMultiTenantContextSetter.MultiTenantContext` from `IMultiTenantStore<ExamPrismTenantInfo>` and set `RequestContext` before invoking the callback so `ICacheService` (`HybridCacheService`) prefixes keys with `"t:{tenantId}:"` and `AppDbContext` resolves the tenant's physical PostgreSQL database (tested in Task 4).
4. **Silo TCP port collisions and unreachable Redis host startup in existing `RedisIntegrationTests`**: Multiple `WebApplicationFactory<Program>` instances run concurrently in xUnit (`CustomWebApplicationFactory`, `RedisWebApplicationFactory`, and `.WithWebHostBuilder` overrides including `UnreachableRedis_ReportsUnhealthy503OnHealthEndpoint_WhileApiEndpointsFailOpenWithout500` which passes `127.0.0.1:6399`). `OrleansSetup` must dynamically allocate free loopback ports when `SiloPort`/`GatewayPort` are `<= 0` or already bound on loopback, and if `ConnectionStrings:Redis` is unreachable during silo configuration (`!multiplexer.IsConnected`), fall back to in-memory clustering/storage/reminders with a warning so the web host still boots and `/health` reports `503 Unhealthy` via `RedisHealthCheck` without crashing `CreateClient()` (tested in Task 3 and Task 4).
5. **Resolving `OrleansHealthCheck` from a bare `ServiceCollection` in `DependencyInjectionTests`**: `OrleansHealthCheck` must accept `ISiloStatusOracle? siloStatusOracle = null` so unit tests that call `services.AddInfrastructure(configuration)` without `hostBuilder.UseExamPrismOrleans()` can resolve health checks without DI activation errors (tested in Task 2 and Task 3).

---

### Task 1: NuGet Packages, `Common.Orleans` Contracts & `TenantGrainFactory`

**Files:**
- Modify: `apps/backend/Directory.Packages.props:6-27`
- Modify: `apps/backend/src/ExamPrism.csproj:1-30`
- Create: `apps/backend/src/Common/Orleans/OrleansTenantConstants.cs`
- Create: `apps/backend/src/Common/Orleans/ITenantScopedGrain.cs`
- Create: `apps/backend/src/Common/Orleans/TenantGrainKey.cs`
- Create: `apps/backend/src/Common/Orleans/ITenantGrainFactory.cs`
- Create: `apps/backend/src/Infrastructure/Orleans/OrleansOptions.cs`
- Create: `apps/backend/src/Infrastructure/Orleans/TenantGrainFactory.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansFoundationUnitTests.cs`

**Interfaces:**
- Consumes: `Common.MultiTenancy.ITenantContext`, `Orleans.IGrainWithStringKey`, `Orleans.IGrainFactory`, `Orleans.Runtime.RequestContext`
- Produces:
  - `Common.Orleans.OrleansTenantConstants` (`TenantIdRequestContextKey = "ExamPrism.TenantId"`, `DefaultStateStore = "Default"`)
  - `Common.Orleans.ITenantScopedGrain : IGrainWithStringKey`
  - `Common.Orleans.TenantGrainKey(Guid TenantId, string EntityKey)` (`Format`, `Parse`, `TryParse`, `ToString`)
  - `Common.Orleans.ITenantGrainFactory` (`GetGrain<TGrainInterface>(string)`, `GetGrain<TGrainInterface>(Guid)`, `GetGrainForTenant<TGrainInterface>(Guid, string)`, `GetGrainForTenant<TGrainInterface>(Guid, Guid)`)
  - `ExamPrism.Infrastructure.Orleans.OrleansOptions` (`SectionName = "Orleans"`, `DefaultStateStoreName = "Default"`, `ClusterId`, `ServiceId`, `StateStoreName`, `SiloPort`, `GatewayPort`)
  - `ExamPrism.Infrastructure.Orleans.TenantGrainFactory(IGrainFactory grainFactory, ITenantContext tenantContext) : ITenantGrainFactory`

- [x] **Step 1: Add Orleans NuGet package versions and references**

In `apps/backend/Directory.Packages.props` under `<!-- API Dependencies -->`, add:
```xml
    <PackageVersion Include="Microsoft.Orleans.Clustering.Redis" Version="10.4.2" />
    <PackageVersion Include="Microsoft.Orleans.Persistence.Redis" Version="10.4.2" />
    <PackageVersion Include="Microsoft.Orleans.Reminders.Redis" Version="10.4.2" />
    <PackageVersion Include="Microsoft.Orleans.Server" Version="10.4.2" />
```
In `apps/backend/src/ExamPrism.csproj`, add `<PackageReference>` entries for those four packages.

- [x] **Step 2: Write failing unit tests for `TenantGrainKey` and `TenantGrainFactory` in `apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansFoundationUnitTests.cs`**

Create `OrleansFoundationUnitTests` (`namespace Tests.Infrastructure.Orleans`) with tests:
- `TenantGrainKey_FormatAndParse_RoundTripsGuidAndCompositeEntityKeyWithColons`: formats `tenantId = Guid.Parse("11111111-2222-3333-4444-555555555555")` and `entityKey = "exam-10:attempt-20"`, asserts formatted string is `"11111111-2222-3333-4444-555555555555:exam-10:attempt-20"`, and `TenantGrainKey.Parse` / `TryParse` extracts both `TenantId` and `"exam-10:attempt-20"`.
- `TenantGrainKey_RejectsEmptyGuid_BlankEntityKey_AndMalformedCompoundKeys`: asserts `Format(Guid.Empty, "k")` and `Format(Guid.NewGuid(), "  ")` throw `ArgumentException`, and `TryParse` returns `false` for `null`, `""`, `"not-a-guid:key"`, `"00000000-0000-0000-0000-000000000000:key"`, `"11111111-2222-3333-4444-555555555555:"`, and `"11111111-2222-3333-4444-555555555555"`.
- `TenantGrainFactory_GetGrain_UsesAmbientTenantContext_FormatsCompoundKey_AndSetsRequestContext`: mocks `IGrainFactory` and `ITenantContext` (`HasTenant = true`, `TenantId = tenantId`), calls `factory.GetGrain<IDummyTenantGrain>("attempt-1")`, asserts `IGrainFactory.GetGrain<IDummyTenantGrain>($"{tenantId:D}:attempt-1")` was called and `RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey)` equals `tenantId.ToString("D")`.
- `TenantGrainFactory_GetGrain_WithoutActiveTenant_ThrowsInvalidOperationException`: mocks `ITenantContext` (`HasTenant = false`, `TenantId = null`), asserts `factory.GetGrain<IDummyTenantGrain>("attempt-1")` throws `InvalidOperationException` with message `"*active tenant context*"`.
- `TenantGrainFactory_GetGrainForTenant_ResolvesWithExplicitTenantIdEvenWhenAmbientTenantAbsent`: mocks `ITenantContext` (`HasTenant = false`), calls `factory.GetGrainForTenant<IDummyTenantGrain>(tenantId, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"))`, asserts compound key is `$"{tenantId:D}:aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"`.

- [x] **Step 3: Run `OrleansFoundationUnitTests` to verify compilation failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~OrleansFoundationUnitTests"`
Expected: FAIL (`OrleansTenantConstants`, `ITenantScopedGrain`, `TenantGrainKey`, `ITenantGrainFactory`, and `TenantGrainFactory` do not exist yet).

- [x] **Step 4: Implement `Common.Orleans` contracts, `OrleansOptions`, and `TenantGrainFactory`**

- Create `apps/backend/src/Common/Orleans/OrleansTenantConstants.cs`, `ITenantScopedGrain.cs`, `TenantGrainKey.cs`, and `ITenantGrainFactory.cs` per Spec §3.1–§3.4.
- In `TenantGrainKey.TryParse(string? compoundKey, out TenantGrainKey result)`: find first `':'` via `compoundKey.IndexOf(':')`; validate `colonIndex == 36`, `Guid.TryParseExact(compoundKey.AsSpan(0, 36), "D", out var tenantId)` with `tenantId != Guid.Empty`, and `var entityKey = compoundKey[(colonIndex + 1)..].Trim()` is non-empty.
- Create `apps/backend/src/Infrastructure/Orleans/OrleansOptions.cs` per Spec §2.3 (`ClusterId = "examprism-cluster"`, `ServiceId = "examprism-service"`, `StateStoreName = "Default"`, `SiloPort = 11111`, `GatewayPort = 30000`).
- Create `apps/backend/src/Infrastructure/Orleans/TenantGrainFactory.cs` per Spec §4.1.

- [x] **Step 5: Run `OrleansFoundationUnitTests` to verify all tests pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~OrleansFoundationUnitTests"`
Expected: PASS.

- [x] **Step 6: Commit**

```bash
git add apps/backend/Directory.Packages.props apps/backend/src/ExamPrism.csproj apps/backend/src/Common/Orleans apps/backend/src/Infrastructure/Orleans/OrleansOptions.cs apps/backend/src/Infrastructure/Orleans/TenantGrainFactory.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansFoundationUnitTests.cs
git commit -m "feat(orleans): add Common.Orleans contracts, TenantGrainKey, and TenantGrainFactory"
```

---

### Task 2: Grain Call Filters, `TenantScopedGrain` Base Class & `OrleansHealthCheck`

**Files:**
- Create: `apps/backend/src/Infrastructure/Orleans/TenantOutgoingGrainCallFilter.cs`
- Create: `apps/backend/src/Infrastructure/Orleans/TenantIncomingGrainCallFilter.cs`
- Create: `apps/backend/src/Infrastructure/Orleans/TenantScopedGrain.cs`
- Create: `apps/backend/src/Infrastructure/Orleans/OrleansHealthCheck.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansFoundationUnitTests.cs`

**Interfaces:**
- Consumes: `Common.Orleans.*`, `ExamPrism.Infrastructure.MultiTenancy.ExamPrismTenantInfo`, `Finbuckle.MultiTenant.Abstractions.IMultiTenantContextAccessor<ExamPrismTenantInfo>`, `Finbuckle.MultiTenant.Abstractions.IMultiTenantContextSetter`, `Finbuckle.MultiTenant.Abstractions.IMultiTenantStore<ExamPrismTenantInfo>`, `Orleans.IOutgoingGrainCallFilter`, `Orleans.IIncomingGrainCallFilter`, `Orleans.Runtime.ISiloStatusOracle`
- Produces:
  - `ExamPrism.Infrastructure.Orleans.TenantOutgoingGrainCallFilter(IMultiTenantContextAccessor<ExamPrismTenantInfo> accessor) : IOutgoingGrainCallFilter`
  - `ExamPrism.Infrastructure.Orleans.TenantIncomingGrainCallFilter(IServiceScopeFactory scopeFactory) : IIncomingGrainCallFilter`
  - `ExamPrism.Infrastructure.Orleans.TenantScopedGrain : Grain, ITenantScopedGrain`
  - `ExamPrism.Infrastructure.Orleans.OrleansHealthCheck(ISiloStatusOracle? siloStatusOracle = null) : IHealthCheck`

- [x] **Step 1: Add failing unit tests for filters and `OrleansHealthCheck` in `apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansFoundationUnitTests.cs`**

Add unit tests to `OrleansFoundationUnitTests`:
- `OutgoingFilter_WhenRequestContextEmptyAndTenantActive_PopulatesTenantIdInRequestContext`: sets active `ExamPrismTenantInfo` on `IMultiTenantContextAccessor<ExamPrismTenantInfo>`, clears `RequestContext`, invokes `TenantOutgoingGrainCallFilter.Invoke(context)`, asserts `RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey)` equals `tenantId.ToString("D")` and `context.Invoke()` was awaited.
- `IncomingFilter_ForNonTenantScopedGrain_InvokesContextDirectlyWithoutKeyValidation`: mocks `IIncomingGrainCallContext` where `context.Grain` is a plain `IGrain`, asserts `context.Invoke()` is called without touching the tenant store.
- `IncomingFilter_ForTenantScopedGrain_HydratesMultiTenantContext_AndRestoresPriorContextInFinallyEvenOnException`: configures `context.TargetId` / `context.Grain` as `ITenantScopedGrain` with key `$"{tenantA:D}:attempt-1"`, mocks `IMultiTenantStore<ExamPrismTenantInfo>.GetAsync(tenantA.ToString("D"))` returning active `ExamPrismTenantInfo`, verifies that during `context.Invoke()` the `IMultiTenantContextAccessor<ExamPrismTenantInfo>.MultiTenantContext.TenantInfo.Id` equals `tenantA.ToString("D")`, and after `Invoke()` completes (or throws `InvalidOperationException("boom")`), `MultiTenantContext` is restored to `priorContext`.
- `IncomingFilter_RejectsMalformedKey_CrossTenantRequestContext_AndInactiveOrUnknownTenant`: asserts `Invoke` throws `InvalidOperationException` when (a) key is `"bad-key"`, (b) `RequestContext` has `tenantB` while grain key has `tenantA`, (c) store returns `null`, or (d) store returns tenant with `Status = TenantStatus.Suspended`.
- `OrleansHealthCheck_ReturnsHealthyWhenSiloActive_AndUnhealthyWhenNotActiveOrNull`: mocks `ISiloStatusOracle` returning `SiloStatus.Active` -> `HealthStatus.Healthy`; returning `SiloStatus.Dead` -> `HealthStatus.Unhealthy`; passing `null` oracle -> `HealthStatus.Unhealthy`.

- [x] **Step 2: Run `OrleansFoundationUnitTests` to verify compilation failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~OrleansFoundationUnitTests"`
Expected: FAIL (`TenantOutgoingGrainCallFilter`, `TenantIncomingGrainCallFilter`, `TenantScopedGrain`, and `OrleansHealthCheck` do not exist yet).

- [x] **Step 3: Implement `TenantOutgoingGrainCallFilter`, `TenantIncomingGrainCallFilter`, `TenantScopedGrain`, and `OrleansHealthCheck`**

- Create `apps/backend/src/Infrastructure/Orleans/TenantOutgoingGrainCallFilter.cs` per Spec §4.2.
- Create `apps/backend/src/Infrastructure/Orleans/TenantIncomingGrainCallFilter.cs` per Spec §4.3:
  - Extract raw key via `(context.Grain as IAddressable)?.GetPrimaryKeyString() ?? context.TargetId.Key.ToString() ?? string.Empty` (noting `context.TargetId.Key` in Orleans can be converted via `context.Grain.GetPrimaryKeyString()`).
  - Validate `TenantGrainKey.TryParse`, check `RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey)`, load `ExamPrismTenantInfo` from `IMultiTenantStore<ExamPrismTenantInfo>`, set `IMultiTenantContextSetter.MultiTenantContext`, and restore `priorContext` in `finally`.
- Create `apps/backend/src/Infrastructure/Orleans/TenantScopedGrain.cs` per Spec §4.4.
- Create `apps/backend/src/Infrastructure/Orleans/OrleansHealthCheck.cs` per Spec §4.6 with constructor `public OrleansHealthCheck(ISiloStatusOracle? siloStatusOracle = null)`.

- [x] **Step 4: Run `OrleansFoundationUnitTests` to verify all tests pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~OrleansFoundationUnitTests"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/Orleans/TenantOutgoingGrainCallFilter.cs apps/backend/src/Infrastructure/Orleans/TenantIncomingGrainCallFilter.cs apps/backend/src/Infrastructure/Orleans/TenantScopedGrain.cs apps/backend/src/Infrastructure/Orleans/OrleansHealthCheck.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansFoundationUnitTests.cs
git commit -m "feat(orleans): add multi-tenant grain call filters, TenantScopedGrain base, and OrleansHealthCheck"
```

---

### Task 3: Co-Hosted Dual-Mode `OrleansSetup`, `TenantProbeGrain`, Host Wiring & `ArchitectureTests`

**Files:**
- Create: `apps/backend/src/Infrastructure/Orleans/OrleansSetup.cs`
- Create: `apps/backend/src/Infrastructure/Orleans/Diagnostics/TenantProbeGrain.cs`
- Modify: `apps/backend/src/appsettings.json`
- Modify: `apps/backend/src/Program.cs:46-52`
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs:106-150`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs:27-38`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs:98-100`
- Modify: `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`

**Interfaces:**
- Consumes: `ExamPrism.Infrastructure.Orleans.*`, `Common.Orleans.*`, `Common.Caching.ICacheService`, `ExamPrism.Infrastructure.Persistence.AppDbContext`
- Produces:
  - `ExamPrism.Infrastructure.Orleans.OrleansSetup.UseExamPrismOrleans(this IHostBuilder hostBuilder)`
  - `ExamPrism.Infrastructure.Orleans.OrleansSetup.AddExamPrismOrleans(this IServiceCollection services, IConfiguration configuration, IHealthChecksBuilder healthChecksBuilder)`
  - `ExamPrism.Infrastructure.Orleans.Diagnostics.ITenantProbeGrain : ITenantScopedGrain`
  - `ExamPrism.Infrastructure.Orleans.Diagnostics.TenantProbeGrain : TenantScopedGrain, ITenantProbeGrain, IRemindable`

- [x] **Step 1: Add failing tests in `ArchitectureTests.cs` and `DependencyInjectionTests.cs`**

- In `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`, add:
  - `TypesInFeatures_MustNotReferenceRawOrleansGrainFactoryOrClusterClient`: scans all types in `ExamPrism.Features.*` and asserts no base type, interface, field, property, constructor parameter, or method signature references `Orleans.IGrainFactory` or `Orleans.IClusterClient`.
  - `TenantScopedGrains_MustBePublicSealedEndWithGrainAndImplementITenantScopedGrain`: finds all non-abstract subclasses of `TenantScopedGrain` in `AppAssembly`, asserts `grainTypes.Should().NotBeEmpty()`, and for each grain asserts `IsPublic`, `IsSealed`, `Name.EndsWith("Grain")`, and implements at least one interface `i != typeof(ITenantScopedGrain) && typeof(ITenantScopedGrain).IsAssignableFrom(i)`.
- In `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`:
  - Assert `services.Should().ContainSingle(s => s.ServiceType == typeof(ITenantGrainFactory)).Which.Lifetime.Should().Be(ServiceLifetime.Scoped);` and `healthCheckOptions.Registrations.Should().Contain(r => r.Name == "orleans");`.

- [x] **Step 2: Run `ArchitectureTests` and `DependencyInjectionTests` to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~DependencyInjectionTests"`
Expected: FAIL (`OrleansSetup` and `TenantProbeGrain` are not wired yet).

- [x] **Step 3: Implement `TenantProbeGrain`, `OrleansSetup`, and wire `Program.cs`, `DependencyInjection.cs`, `appsettings.json`, and `CustomWebApplicationFactory.cs`**

- Create `apps/backend/src/Infrastructure/Orleans/Diagnostics/TenantProbeGrain.cs`:
  - Define `[GenerateSerializer] public sealed record TenantProbeState(string? Value, int Counter, string? LastReminderName);` and `[GenerateSerializer] public sealed record TenantProbeSnapshot(Guid TenantId, string EntityKey, string? StateValue, int Counter, string? CachedValue, int TenantUserCount, string? LastReminderName);`.
  - Define `public interface ITenantProbeGrain : ITenantScopedGrain` with methods:
    - `Task<TenantProbeSnapshot> SetAndInspectAsync(string value, string cacheKey, string cacheValue);`
    - `Task<TenantProbeSnapshot> GetSnapshotAsync(string cacheKey);`
    - `Task<TenantProbeSnapshot> SimulateTimerScopeCallbackAsync(string cacheKey, string cacheValue);`
    - `Task RegisterProbeReminderAsync(string reminderName);`
    - `Task DeactivateImmediatelyAsync();`
  - Implement `public sealed class TenantProbeGrain : TenantScopedGrain, ITenantProbeGrain, IRemindable` injecting `[PersistentState("probe", OrleansTenantConstants.DefaultStateStore)] IPersistentState<TenantProbeState> state` and `ICacheService cacheService`:
    - `SetAndInspectAsync`: updates `_state.State`, calls `await _state.WriteStateAsync()`, caches `cacheValue` under `cacheKey` via `_cacheService.GetOrCreateAsync`, queries `await db.Users.CountAsync()` via `ExecuteInTenantScopeAsync`, and returns `TenantProbeSnapshot`.
    - `SimulateTimerScopeCallbackAsync`: clears `RequestContext.Clear()` and resets `IMultiTenantContextSetter.MultiTenantContext = null!` to simulate a raw timer tick, then calls `ExecuteInTenantScopeAsync` to write `cacheValue` via `ICacheService` and query `AppDbContext.Users.CountAsync()`, returning `TenantProbeSnapshot`.
    - `RegisterProbeReminderAsync`: calls `await this.RegisterOrUpdateReminder(reminderName, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1))` and immediately unregisters it (`await this.UnregisterReminder(reminder)`), updating `_state.State = _state.State with { LastReminderName = reminderName }; await _state.WriteStateAsync();` to prove reminder table storage works end-to-end.
    - `DeactivateImmediatelyAsync`: calls `DeactivateOnIdle()`.
- Create `apps/backend/src/Infrastructure/Orleans/OrleansSetup.cs`:
  - Implement `UseExamPrismOrleans(this IHostBuilder hostBuilder)` and `AddExamPrismOrleans(this IServiceCollection services, IConfiguration configuration, IHealthChecksBuilder healthChecksBuilder)` per Spec §4.5.
  - Include helper `ResolveAvailableLoopbackPort(int configuredPort)` that checks if `configuredPort > 0` can bind on `IPAddress.Loopback`; if `configuredPort <= 0` or binding throws `SocketException`, allocates an ephemeral port via `new TcpListener(IPAddress.Loopback, 0)` so concurrent test hosts never collide.
  - When `ConnectionStrings:Redis` is non-whitespace, test connectivity via a short-timeout `ConnectionMultiplexer.Connect(redisOptions)` (or check `IsConnected`): if reachable, configure `UseRedisClustering`, `AddRedisGrainStorage`, and `UseRedisReminderService`; if unreachable at startup (such as in `RedisIntegrationTests.UnreachableRedis_...`), log a warning and fall back to `UseLocalhostClustering`, `AddMemoryGrainStorage`, and `UseInMemoryReminderService` so the web host starts cleanly while `RedisHealthCheck` reports `503 Unhealthy`.
- Update `apps/backend/src/appsettings.json` with the `"Orleans"` section (Spec §2.2).
- Update `apps/backend/src/Program.cs` to call `builder.Host.UseExamPrismOrleans();` right after `builder.Host.UseWolverine(...);`.
- Update `apps/backend/src/Infrastructure/DependencyInjection.cs` to call `services.AddExamPrismOrleans(configuration, healthChecksBuilder);`.
- Update `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs` to add `builder.UseSetting("Orleans:SiloPort", "0");` and `builder.UseSetting("Orleans:GatewayPort", "0");`.

- [x] **Step 4: Run `ArchitectureTests`, `DependencyInjectionTests`, and `HealthEndpointTests` to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~DependencyInjectionTests|FullyQualifiedName~HealthEndpointTests"`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add apps/backend/src/appsettings.json apps/backend/src/Program.cs apps/backend/src/Infrastructure/DependencyInjection.cs apps/backend/src/Infrastructure/Orleans/OrleansSetup.cs apps/backend/src/Infrastructure/Orleans/Diagnostics/TenantProbeGrain.cs apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs
git commit -m "feat(orleans): wire co-hosted dual-mode Orleans silo, TenantProbeGrain, and architecture rules"
```

---

### Task 4: Multi-Tenant In-Memory & Distributed Redis Integration Tests

**Files:**
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansMultiTenantIntegrationTests.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansRedisIntegrationTests.cs`

**Interfaces:**
- Consumes: `Common.Orleans.ITenantGrainFactory`, `ExamPrism.Infrastructure.Orleans.Diagnostics.ITenantProbeGrain`, `Common.Caching.ICacheService`, `Tests.Infrastructure.CustomWebApplicationFactory`, `Tests.Infrastructure.RedisWebApplicationFactory`
- Produces: End-to-end verification of multi-tenant grain isolation, `HybridCacheService` + `AppDbContext` integration inside grains and timer callbacks, and Redis-backed grain storage, clustering, and reminders.

- [x] **Step 1: Write `OrleansMultiTenantIntegrationTests` and `OrleansRedisIntegrationTests`**

- Create `apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansMultiTenantIntegrationTests.cs` (`IClassFixture<CustomWebApplicationFactory>`) with tests:
  - `GrainState_And_HybridCache_AreStrictlyIsolatedAcrossTenantsForIdenticalEntityKey`: creates tenant scopes for `"tenant-a"` and `"tenant-b"`, resolves `ITenantGrainFactory` in each scope, calls `GetGrain<ITenantProbeGrain>("attempt-shared")`, invokes `SetAndInspectAsync("ans-A", "exam:1:rubric", "rubric-A")` on tenant A and `SetAndInspectAsync("ans-B", "exam:1:rubric", "rubric-B")` on tenant B; asserts `TenantId`, `StateValue`, and `CachedValue` are completely isolated between tenant A and tenant B, and verifying via `ICacheService.GetOrDefaultAsync<string>("exam:1:rubric")` in each tenant scope returns `"rubric-A"` and `"rubric-B"` respectively.
  - `ExecuteInTenantScopeAsync_WhenRequestContextAndAsyncLocalCleared_RehydratesTenantForHybridCacheAndAppDbContext`: registers a user in `tenant-a` (so `tenant-a` has 1 more user than `tenant-b`), invokes `SimulateTimerScopeCallbackAsync("timer:key", "timer-val-A")` on `tenant-a`'s grain and `SimulateTimerScopeCallbackAsync("timer:key", "timer-val-B")` on `tenant-b`'s grain; asserts `ICacheService` inside `tenant-a` scope reads `"timer-val-A"` (not `"platform:"`) and `TenantUserCount` reflects each tenant's physical PostgreSQL database.
  - `GrainCall_ForSuspendedOrUnknownTenant_ThrowsInvalidOperationException`: resolves `ITenantGrainFactory` from root/scope, calls `GetGrainForTenant<ITenantProbeGrain>(suspendedTenantId, "attempt-1")` and `GetGrainForTenant<ITenantProbeGrain>(Guid.NewGuid(), "attempt-1")`, and asserts calling `GetSnapshotAsync("k")` throws `InvalidOperationException`.
- Create `apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansRedisIntegrationTests.cs` (`IClassFixture<RedisWebApplicationFactory>`) with tests:
  - `RedisMode_PersistsGrainStateAcrossDeactivation_RegistersReminder_AndReportsHealthyOnHealthEndpoint`: using `RedisWebApplicationFactory`, creates a `"tenant-a"` scope, resolves `ITenantProbeGrain` for `"redis-attempt-1"`, calls `SetAndInspectAsync("persisted-in-redis", "exam:redis", "cached")`, calls `RegisterProbeReminderAsync("auto-submit-reminder")`, forces grain deactivation via `DeactivateImmediatelyAsync()`, calls `GetSnapshotAsync("exam:redis")` on a new grain reference to reactivate from Redis storage, and asserts `StateValue == "persisted-in-redis"` and `LastReminderName == "auto-submit-reminder"`. Also asserts `IConnectionMultiplexer` contains Redis keys created by Orleans and `GET /health` returns `200 OK` (`Status == "Healthy"`).

- [x] **Step 2: Run the new Orleans integration test suites**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~OrleansMultiTenantIntegrationTests|FullyQualifiedName~OrleansRedisIntegrationTests"`
Expected: PASS.

- [x] **Step 3: Run the entire backend test suite to verify zero regressions**

Run: `dotnet test apps/backend/ExamPrism.slnx`
Expected: PASS (all existing unit, architecture, multi-tenancy, and Redis integration tests pass alongside the new Orleans suites).

- [x] **Step 4: Commit**

```bash
git add apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansMultiTenantIntegrationTests.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/Orleans/OrleansRedisIntegrationTests.cs
git commit -m "test(orleans): add multi-tenant in-memory and Redis integration test suites"
```
