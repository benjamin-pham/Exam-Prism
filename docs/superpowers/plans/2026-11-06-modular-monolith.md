# Modular Monolith & Dynamic `IModule` Loading Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the single-project `ExamPrism` backend into a Modular Monolith (`ExamPrism.Contracts`, `ExamPrism.Infrastructure`, `ExamPrism.Modules.Tenants`, `ExamPrism.Modules.Identity`, and `ExamPrism` Host) with dynamic `.dll` scanning via `IModule` and zero cross-module service coupling (using Wolverine `IMessageBus` message contracts).

**Architecture:** `ExamPrism.Contracts` defines `IModule`, shared domain/handler/endpoint primitives, and cross-module integration message records. `ExamPrism.Infrastructure` provides technical cross-cutting infrastructure and `ModuleLoader`, which dynamically loads `ExamPrism.Modules.*.dll` from `AppContext.BaseDirectory`, registers `ModuleSettingFiles` into `IConfiguration`, invokes `IModule.ConfigureServices(WebApplicationBuilder)`, and wires Wolverine handlers, FluentValidation validators, and `IEndpoint` routes. `Modules.Tenants` owns `CatalogDbContext` (`Tenant`, `PlatformUser`, `PlatformRole`, `PlatformRefreshToken`) while `Modules.Identity` owns `AppDbContext` (`ApplicationUser`, `RefreshToken`), communicating during tenant provisioning and fleet migration solely via Wolverine commands.

**Tech Stack:** .NET 10, ASP.NET Core 10, Entity Framework Core 10 (Npgsql), ASP.NET Core Identity, Finbuckle.MultiTenant 10.1.5, WolverineFx 4.8.0, FluentValidation 12.2.0, Microsoft Orleans 10.4.2, xUnit, FluentAssertions, NSubstitute, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-06-modular-monolith-design.md`

## Global Constraints

- Target framework: `net10.0` with `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` via `apps/backend/Directory.Build.props`, and central package management via `apps/backend/Directory.Packages.props`.
- `IModule` signature in `ExamPrism.Contracts` (`namespace Common.Modules`) must be exact:
  ```csharp
  public interface IModule
  {
      string Name { get; }
      string[] ModuleSettingFiles { get; }
      void ConfigureServices(WebApplicationBuilder builder);
  }
  ```
- `ExamPrism.Contracts` must have zero `<ProjectReference>` dependencies.
- `ExamPrism.Infrastructure` must reference only `ExamPrism.Contracts`.
- `ExamPrism.Modules.Tenants` and `ExamPrism.Modules.Identity` must reference only `ExamPrism.Contracts` and `ExamPrism.Infrastructure` — never each other.
- `ExamPrism` (Host) must not have a compile-time assembly reference to `ExamPrism.Modules.*` (`ReferenceOutputAssembly="false"` + MSBuild output copy target only).
- Preserve existing C# namespaces (`Common.*`, `ExamPrism.Domain.*`, `ExamPrism.Infrastructure.*`, `ExamPrism.Features.*`, `ExamPrism.Endpoints.*`) inside the extracted projects so EF Core migration snapshots and existing test `using` directives remain compatible.

## Review Focus

1. **Module setting files overriding test/environment configuration:** If `ModuleLoader` appends `ModuleSettingFiles` to the end of `builder.Configuration.Sources` via `AddJsonFile`, module JSON defaults will override `EnvironmentVariablesConfigurationSource`, CLI args, and `WebApplicationFactory.UseSetting(...)`. `ModuleLoader` must insert module JSON sources immediately after existing `JsonConfigurationSource` entries (before environment variables and in-memory test sources). Tested in Task 1 (`ModuleLoader_LoadsModuleSettingFiles_WithoutOverridingInMemoryTestSettings`).
2. **Module assembly loading order vs. Wolverine & Orleans initialization:** If `builder.Host.UseWolverine()` or `builder.Host.UseOrleans()` runs before `ModuleLoader` loads `ExamPrism.Modules.*.dll` into `AssemblyLoadContext.Default`, Wolverine handler discovery and Orleans grain discovery (`TenantProbeGrain`) will miss module types. `builder.AddModules()` must execute at the very start of `builder.AddInfrastructure()`. Tested in Task 1 & Task 4.
3. **Direct `services.AddInfrastructure(configuration)` calls in unit tests without `WebApplicationBuilder`:** Existing tests in `DependencyInjectionTests` call `services.AddInfrastructure(configuration)` directly on a `ServiceCollection`. `AddInfrastructure(IServiceCollection, IConfiguration)` must also invoke discovered modules' service registrations so standalone `ServiceCollection` tests resolve `CatalogDbContext`, `AppDbContext`, `ITenantProvisioner`, and `UserManager<T>` identically. Tested in Task 3 & Task 4.
4. **Cross-module failure propagation during tenant provisioning and fleet migration:** When `InitializeTenantIdentityHandler` or `MigrateTenantDatabaseHandler` returns a failed `Result` or throws over `IMessageBus`, `TenantProvisioner` must transition the tenant to `TenantStatus.Failed` (preserving `EncryptedOwnerPassword`) and `TenantFleetMigrator` must return `TenantMigrationItemResult(Succeeded: false, ...)`. Tested in Task 3.
5. **Default MSBuild SDK globbing when splitting `apps/backend/src/`:** Keeping `ExamPrism.csproj` directly in `apps/backend/src/` would cause the .NET SDK to glob `.cs` files from sibling project directories (`ExamPrism.Contracts/`, `Modules/`, etc.). Moving Host files into `apps/backend/src/ExamPrism/` cleanly isolates each project's source tree. Tested in Task 4 (`AspireAppHostConfigurationTests` & `ArchitectureTests`).

---

### Task 1: Create `ExamPrism.Contracts` and `ExamPrism.Infrastructure` with `IModule`, `ModuleLoader`, and Cross-Module Message Contracts

**Files:**
- Create: `apps/backend/src/ExamPrism.Contracts/ExamPrism.Contracts.csproj`
- Create: `apps/backend/src/ExamPrism.Contracts/Modules/IModule.cs`
- Create: `apps/backend/src/ExamPrism.Contracts/Modules/IStartupInitializer.cs`
- Create: `apps/backend/src/ExamPrism.Contracts/Messages/Tenants/TenantIntegrationMessages.cs`
- Move: `apps/backend/src/Common/**` (except `IAppDbContext.cs` and `ICatalogDbContext.cs`) -> `apps/backend/src/ExamPrism.Contracts/`
- Create: `apps/backend/src/ExamPrism.Infrastructure/ExamPrism.Infrastructure.csproj`
- Create: `apps/backend/src/ExamPrism.Infrastructure/Modules/ModuleRegistry.cs`
- Create: `apps/backend/src/ExamPrism.Infrastructure/Modules/ModuleLoader.cs`
- Move: shared technical files from `apps/backend/src/Infrastructure/**` -> `apps/backend/src/ExamPrism.Infrastructure/`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/Modules/ModuleLoaderTests.cs`

**Interfaces:**
- Produces:
  - `Common.Modules.IModule`:
    - `string Name { get; }`
    - `string[] ModuleSettingFiles { get; }`
    - `void ConfigureServices(WebApplicationBuilder builder);`
  - `Common.Modules.IStartupInitializer`:
    - `Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default);`
  - `Common.Messages.Tenants`:
    - `public sealed record InitializeTenantIdentityCommand(Guid TenantId, string TenantSlug, string RawConnectionString, string? OwnerEmail, string? EncryptedOwnerPassword);`
    - `public sealed record InitializeTenantIdentityResult(bool Succeeded);`
    - `public sealed record MigrateTenantDatabaseCommand(Guid TenantId, string TenantSlug, string RawConnectionString);`
    - `public sealed record MigrateTenantDatabaseResult(int AppliedMigrationsCount);`
  - `ExamPrism.Infrastructure.Modules.ModuleRegistry`:
    - `public IReadOnlyList<IModule> Modules { get; }`
    - `public IReadOnlyList<Assembly> ModuleAssemblies { get; }`
  - `ExamPrism.Infrastructure.Modules.ModuleLoader`:
    - `public static ModuleRegistry AddModules(this WebApplicationBuilder builder, IEnumerable<Assembly>? additionalAssemblies = null, string? searchDirectory = null)`
    - `public static ModuleRegistry DiscoverAndLoadModules(IEnumerable<Assembly>? additionalAssemblies = null, string? searchDirectory = null)`

- [x] **Step 1: Create `ExamPrism.Contracts` project and move shared `Common` primitives**

Create `apps/backend/src/ExamPrism.Contracts/ExamPrism.Contracts.csproj` with `<FrameworkReference Include="Microsoft.AspNetCore.App" />` and package references `FluentValidation.DependencyInjectionExtensions`, `Microsoft.EntityFrameworkCore`, `Microsoft.Orleans.Server`, and `WolverineFx`. Move all files from `apps/backend/src/Common/` (excluding `Common/Persistence/IAppDbContext.cs` and `Common/Persistence/ICatalogDbContext.cs`) into `apps/backend/src/ExamPrism.Contracts/`, keeping the embedded resource logical names for `SharedResources.resx` and `SharedResources.vi.resx`:
```xml
<EmbeddedResource Update="Localization/Resources/SharedResources.resx">
  <LogicalName>ExamPrism.Common.Localization.Resources.Common.Localization.SharedResources.resources</LogicalName>
</EmbeddedResource>
<EmbeddedResource Update="Localization/Resources/SharedResources.vi.resx">
  <LogicalName>ExamPrism.Common.Localization.Resources.Common.Localization.SharedResources.vi.resources</LogicalName>
</EmbeddedResource>
```
Add `IModule.cs` (`namespace Common.Modules`), `IStartupInitializer.cs` (`namespace Common.Modules`), and `TenantIntegrationMessages.cs` (`namespace Common.Messages.Tenants`).

- [x] **Step 2: Create `ExamPrism.Infrastructure` project with `ModuleRegistry` and `ModuleLoader`**

Create `apps/backend/src/ExamPrism.Infrastructure/ExamPrism.Infrastructure.csproj` referencing `ExamPrism.Contracts.csproj` and the infrastructure NuGet packages (`Dapper`, `Finbuckle.MultiTenant`, `Finbuckle.MultiTenant.AspNetCore`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, `Microsoft.AspNetCore.OpenApi`, `Microsoft.Extensions.Caching.Hybrid`, `Microsoft.Extensions.Caching.StackExchangeRedis`, `Scalar.AspNetCore`, `Serilog.AspNetCore`, `Serilog.Formatting.Compact`, `Serilog.Sinks.Seq`, `StackExchange.Redis`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`, `Microsoft.Orleans.*`, `WolverineFx.*`, `Microsoft.Extensions.Resilience`, `Microsoft.Extensions.Http.Resilience`, `OpenTelemetry.*`).

Implement `ModuleRegistry` and `ModuleLoader` in `apps/backend/src/ExamPrism.Infrastructure/Modules/`:
- `DiscoverAndLoadModules` scans `searchDirectory ?? AppContext.BaseDirectory` for `ExamPrism.Modules.*.dll`, loads unseen assemblies via `AssemblyLoadContext.Default.LoadFromAssemblyPath`, merges with `additionalAssemblies`, finds concrete `IModule` types, instantiates them via `Activator.CreateInstance`, validates unique non-empty `Name` (throwing `InvalidOperationException` on duplicates), and sorts by `Name` (`StringComparer.OrdinalIgnoreCase`).
- `AddModules(this WebApplicationBuilder builder, ...)`:
  - Resolves each file in `module.ModuleSettingFiles` from `builder.Environment.ContentRootPath` first, then `AppContext.BaseDirectory`; throws `FileNotFoundException` if missing.
  - Inserts the `JsonConfigurationSource` (and optional `{name}.{env}.json`) into `builder.Configuration.Sources` immediately after the last existing `JsonConfigurationSource` so environment variables and `MemoryConfigurationSource` (`UseSetting` / `AddInMemoryCollection`) retain higher precedence.
  - Calls `module.ConfigureServices(builder)` and `builder.Services.AddValidatorsFromAssembly(module.GetType().Assembly, includeInternalTypes: true)`.
  - Registers `ModuleRegistry` as singleton in `builder.Services`.

- [x] **Step 3: Write failing unit tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/Modules/ModuleLoaderTests.cs`**

```csharp
[Fact]
public void DiscoverAndLoadModules_InstantiatesAndOrdersModulesByName_AndRejectsDuplicateNames()
{
    var registry = ModuleLoader.DiscoverAndLoadModules([typeof(FakeAlphaModule).Assembly]);
    registry.Modules.Select(m => m.Name).Should().ContainInOrder("FakeAlpha", "FakeBeta");

    Action duplicateAct = () => ModuleLoader.InstantiateModules([typeof(FakeAlphaModule), typeof(DuplicateAlphaModule)]);
    duplicateAct.Should().Throw<InvalidOperationException>().WithMessage("*FakeAlpha*");
}

[Fact]
public void AddModules_ThrowsFileNotFoundException_WhenModuleSettingFileIsMissing()
{
    var builder = WebApplication.CreateBuilder();
    Action act = () => ModuleLoader.RegisterModules(builder, [new MissingSettingsModule()]);
    act.Should().Throw<FileNotFoundException>().WithMessage("*nonexistent.module.json*");
}

[Fact]
public void AddModules_LoadsModuleSettingFiles_WithoutOverridingInMemoryTestSettings()
{
    var tempDir = Directory.CreateTempSubdirectory();
    File.WriteAllText(Path.Combine(tempDir.FullName, "appsettings.fakemodule.json"),
        """{"FakeSection": {"FromFile": "file-val", "OverriddenKey": "from-file"}}""");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = tempDir.FullName });
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["FakeSection:OverriddenKey"] = "from-memory"
    });

    ModuleLoader.RegisterModules(builder, [new FakeSettingsModule()]);

    builder.Configuration["FakeSection:FromFile"].Should().Be("file-val");
    builder.Configuration["FakeSection:OverriddenKey"].Should().Be("from-memory");
}
```

- [x] **Step 4: Run `ModuleLoaderTests` to verify they pass**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~ModuleLoaderTests"`
Expected: PASS (3 passed, 0 failed)

- [x] **Step 5: Commit Task 1**

```bash
git add apps/backend/
git commit -m "feat(contracts,infrastructure): extract Contracts and Infrastructure with IModule and ModuleLoader"
```

---

### Task 2: Extract `ExamPrism.Modules.Identity` (`IdentityModule`, `AppDbContext`, Auth Features/Endpoints, and Tenant Provisioning/Migration Handlers)

**Files:**
- Create: `apps/backend/src/Modules/Identity/ExamPrism.Modules.Identity.csproj`
- Create: `apps/backend/src/Modules/Identity/appsettings.identity.json`
- Create: `apps/backend/src/Modules/Identity/IdentityModule.cs`
- Create: `apps/backend/src/Modules/Identity/Features/Tenants/InitializeTenantIdentityHandler.cs`
- Create: `apps/backend/src/Modules/Identity/Features/Tenants/MigrateTenantDatabaseHandler.cs`
- Move:
  - `Domain/Identity/ApplicationUser.cs`, `Domain/Identity/RefreshToken.cs` -> `apps/backend/src/Modules/Identity/Domain/Identity/`
  - `Common/Persistence/IAppDbContext.cs` -> `apps/backend/src/Modules/Identity/Persistence/IAppDbContext.cs`
  - `Infrastructure/Persistence/AppDbContext.cs`, `AppDbContextFactory.cs`, `App/Configurations/*`, `Migrations/Tenant/*` -> `apps/backend/src/Modules/Identity/Persistence/`
  - `Infrastructure/Orleans/Diagnostics/TenantProbeGrain.cs` -> `apps/backend/src/Modules/Identity/Orleans/Diagnostics/TenantProbeGrain.cs`
  - `Features/Auth/*` -> `apps/backend/src/Modules/Identity/Features/Auth/`
  - `Endpoints/Auth/*` -> `apps/backend/src/Modules/Identity/Endpoints/Auth/`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/Modules/IdentityModuleTests.cs`

**Interfaces:**
- Consumes:
  - `Common.Modules.IModule`
  - `Common.Messages.Tenants.InitializeTenantIdentityCommand`, `InitializeTenantIdentityResult`
  - `Common.Messages.Tenants.MigrateTenantDatabaseCommand`, `MigrateTenantDatabaseResult`
- Produces:
  - `ExamPrism.Modules.Identity.IdentityModule : IModule` (`Name => "Identity"`, `ModuleSettingFiles => ["appsettings.identity.json"]`)
  - `ExamPrism.Features.Tenants.InitializeTenantIdentityHandler : IHandler<InitializeTenantIdentityCommand, InitializeTenantIdentityResult>`
  - `ExamPrism.Features.Tenants.MigrateTenantDatabaseHandler : IHandler<MigrateTenantDatabaseCommand, MigrateTenantDatabaseResult>`

- [x] **Step 1: Create `ExamPrism.Modules.Identity.csproj`, `appsettings.identity.json`, and move Tenant Identity files**

Create `apps/backend/src/Modules/Identity/ExamPrism.Modules.Identity.csproj` referencing `ExamPrism.Contracts` and `ExamPrism.Infrastructure`, with `<Content Include="appsettings.identity.json" CopyToOutputDirectory="PreserveNewest" />`. Move `ApplicationUser`, `RefreshToken`, `IAppDbContext`, `AppDbContext`, `AppDbContextFactory`, `App/Configurations/*`, `Migrations/Tenant/*`, `TenantProbeGrain`, `Features/Auth/*`, and `Endpoints/Auth/*` into `apps/backend/src/Modules/Identity/`.

- [x] **Step 2: Implement `IdentityModule`, `InitializeTenantIdentityHandler`, and `MigrateTenantDatabaseHandler`**

In `IdentityModule.cs` (`namespace ExamPrism.Modules.Identity`):
- `Name => "Identity"`
- `ModuleSettingFiles => ["appsettings.identity.json"]`
- `ConfigureServices(WebApplicationBuilder builder)` calls `builder.Services.AddTenantIdentityModule(builder.Configuration)` which registers `AppDbContext`, `IAppDbContext`, `AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders()`, and `IPasswordHasher<ApplicationUser>` (`BulkheadPasswordHasher<ApplicationUser>`).

In `InitializeTenantIdentityHandler.cs` (`namespace ExamPrism.Features.Tenants`, `public sealed class InitializeTenantIdentityHandler : IHandler<InitializeTenantIdentityCommand, InitializeTenantIdentityResult>`):
- Normalizes `command.RawConnectionString` via `ITenantConnectionStringNormalizer`, runs `await tenantDb.Database.MigrateAsync(ct)` on a new `AppDbContext`, creates an async DI scope with `IMultiTenantContextSetter` populated for `(command.TenantId, command.TenantSlug, command.RawConnectionString)`, seeds role `TenantRoles.TenantOwner` and user `command.OwnerEmail` (decrypting `command.EncryptedOwnerPassword` via `IConnectionStringProtector`), and returns `new InitializeTenantIdentityResult(true)`.

In `MigrateTenantDatabaseHandler.cs` (`namespace ExamPrism.Features.Tenants`, `public sealed class MigrateTenantDatabaseHandler : IHandler<MigrateTenantDatabaseCommand, MigrateTenantDatabaseResult>`):
- Normalizes `command.RawConnectionString`, counts `GetPendingMigrationsAsync(ct)` on `AppDbContext`, runs `MigrateAsync(ct)`, and returns `new MigrateTenantDatabaseResult(pendingCount)`.

- [x] **Step 3: Write tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/Modules/IdentityModuleTests.cs`**

```csharp
[Fact]
public void IdentityModule_ExposesExpectedNameAndSettingFiles_AndRegistersAppDbContextAndUserManager()
{
    var module = new IdentityModule();
    module.Name.Should().Be("Identity");
    module.ModuleSettingFiles.Should().ContainSingle().Which.Should().Be("appsettings.identity.json");

    var builder = WebApplication.CreateBuilder();
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:CatalogConnection"] = "Host=localhost;Database=test;Username=postgres;Password=postgres",
        ["Jwt:Issuer"] = "ExamPrism",
        ["Jwt:Audience"] = "ExamPrism",
        ["Jwt:SecretKey"] = "SuperSecretKeyForTestingJwtTokensMin256BitsLong!"
    });
    builder.Services.AddInfrastructure(builder.Configuration);
    module.ConfigureServices(builder);

    builder.Services.Should().Contain(s => s.ServiceType == typeof(AppDbContext));
    builder.Services.Should().Contain(s => s.ServiceType == typeof(UserManager<ApplicationUser>));
}
```

- [x] **Step 4: Run `IdentityModuleTests` and `AuthEndpointTests`**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~IdentityModuleTests"`
Expected: PASS

- [x] **Step 5: Commit Task 2**

```bash
git add apps/backend/
git commit -m "feat(identity): extract ExamPrism.Modules.Identity DLL with IModule and tenant db handlers"
```

---

### Task 3: Extract `ExamPrism.Modules.Tenants` (`TenantsModule`, `CatalogDbContext`, Message-Driven `TenantProvisioner` & `TenantFleetMigrator`, and Tenant/Platform Features/Endpoints)

**Files:**
- Create: `apps/backend/src/Modules/Tenants/ExamPrism.Modules.Tenants.csproj`
- Create: `apps/backend/src/Modules/Tenants/appsettings.tenants.json`
- Create: `apps/backend/src/Modules/Tenants/TenantsModule.cs`
- Move & Modify:
  - `Domain/Tenants/**` -> `apps/backend/src/Modules/Tenants/Domain/Tenants/`
  - `Domain/Identity/PlatformUser.cs`, `PlatformRole.cs`, `PlatformRefreshToken.cs` -> `apps/backend/src/Modules/Tenants/Domain/Identity/`
  - `Common/Persistence/ICatalogDbContext.cs` -> `apps/backend/src/Modules/Tenants/Persistence/ICatalogDbContext.cs`
  - `Infrastructure/Persistence/Catalog/**`, `Migrations/Catalog/**` -> `apps/backend/src/Modules/Tenants/Persistence/Catalog/`
  - `Infrastructure/Identity/PlatformAdminOptions.cs`, `PlatformAdminSeeder.cs` -> `apps/backend/src/Modules/Tenants/Identity/`
  - `Infrastructure/MultiTenancy/CatalogMultiTenantStore.cs`, `TenantProvisioner.cs`, `TenantFleetMigrator.cs` -> `apps/backend/src/Modules/Tenants/MultiTenancy/`
  - `Features/Tenants/**`, `Features/Platform/**` -> `apps/backend/src/Modules/Tenants/Features/`
  - `Endpoints/Tenants/**`, `Endpoints/Platform/**` -> `apps/backend/src/Modules/Tenants/Endpoints/`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/TenantProvisionerAndFleetMigratorTests.cs`

**Interfaces:**
- Consumes:
  - `Common.Modules.IModule`, `Common.Modules.IStartupInitializer`
  - `Common.Messages.Tenants.InitializeTenantIdentityCommand`, `InitializeTenantIdentityResult`
  - `Common.Messages.Tenants.MigrateTenantDatabaseCommand`, `MigrateTenantDatabaseResult`
- Produces:
  - `ExamPrism.Modules.Tenants.TenantsModule : IModule` (`Name => "Tenants"`, `ModuleSettingFiles => ["appsettings.tenants.json"]`)
  - `ExamPrism.Infrastructure.MultiTenancy.TenantProvisioner` (sends `InitializeTenantIdentityCommand` via `IMessageBus`)
  - `ExamPrism.Infrastructure.MultiTenancy.TenantFleetMigrator` (sends `MigrateTenantDatabaseCommand` via `IMessageBus`)

- [x] **Step 1: Create `ExamPrism.Modules.Tenants.csproj`, `appsettings.tenants.json`, and move Tenants/Platform files**

Create `apps/backend/src/Modules/Tenants/ExamPrism.Modules.Tenants.csproj` referencing **only** `ExamPrism.Contracts` and `ExamPrism.Infrastructure` (no reference to `ExamPrism.Modules.Identity`), with `<Content Include="appsettings.tenants.json" CopyToOutputDirectory="PreserveNewest" />`. Move all Tenant and Platform domain entities, `CatalogDbContext`, Catalog migrations, `PlatformAdminSeeder`, `CatalogMultiTenantStore`, `TenantProvisioner`, `TenantFleetMigrator`, and Tenant/Platform features and endpoints into `apps/backend/src/Modules/Tenants/`.

- [x] **Step 2: Refactor `TenantProvisioner` and `TenantFleetMigrator` to use `IMessageBus` instead of `AppDbContext` / `ApplicationUser`**

In `TenantProvisioner`:
- Replace `IServiceScopeFactory` / direct `AppDbContext` / `UserManager<ApplicationUser>` usage with `IMessageBus? bus = null` (or resolve `IMessageBus` from `IServiceScopeFactory` / constructor injection).
- After creating the PostgreSQL role and database, invoke:
  ```csharp
  var initCommand = new InitializeTenantIdentityCommand(
      tenant.Id,
      tenant.Slug,
      rawTenantConnStr,
      tenant.OwnerEmail,
      tenant.EncryptedOwnerPassword);
  var initResult = await bus.InvokeAsync<Result<InitializeTenantIdentityResult>>(initCommand, ct);
  if (initResult.IsFailure)
  {
      throw new InvalidOperationException(initResult.Error.Message);
  }
  ```
In `TenantFleetMigrator`:
- Replace direct `new AppDbContext(dbOptions)` migration with dispatching `MigrateTenantDatabaseCommand(tenant.Id, tenant.Slug, rawConnStr)` via `IMessageBus.InvokeAsync<Result<MigrateTenantDatabaseResult>>` (resolved from `IServiceScopeFactory` or constructor).

- [x] **Step 3: Implement `TenantsModule : IModule` and `IStartupInitializer`s**

In `TenantsModule.cs` (`namespace ExamPrism.Modules.Tenants`):
- `Name => "Tenants"`
- `ModuleSettingFiles => ["appsettings.tenants.json"]`
- `ConfigureServices(WebApplicationBuilder builder)` registers `CatalogDbContext`, `ICatalogDbContext`, `catalog_db` health check, `PlatformUser`/`PlatformRole` Identity stores on `CatalogDbContext`, `PlatformAdminSeeder`, `CatalogMultiTenantStore`, `TenantProvisioner`, `TenantFleetMigrator`, Finbuckle store wiring, and `IStartupInitializer`s for PlatformAdmin seeding and MultiTenancy startup recovery/fleet migration.

- [x] **Step 4: Add unit tests in `TenantProvisionerAndFleetMigratorTests.cs` for cross-module message failure handling**

```csharp
[Fact]
public async Task TenantFleetMigrator_MigrateTenantAsync_DispatchesMigrateTenantDatabaseCommandViaBus_AndReturnsAppliedCount()
{
    await using var db = CreateCatalogDbContext();
    var tenant = new Tenant
    {
        Id = Guid.NewGuid(),
        Slug = $"bus-{Guid.NewGuid():N}"[..12],
        Name = "Bus Org",
        Status = TenantStatus.Active,
        EncryptedConnectionString = "enc-conn",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };
    db.Tenants.Add(tenant);
    await db.SaveChangesAsync();

    var protector = Substitute.For<IConnectionStringProtector>();
    protector.Unprotect("enc-conn").Returns("Host=localhost;Database=tenant_bus");
    var normalizer = Substitute.For<ITenantConnectionStringNormalizer>();
    normalizer.Normalize(Arg.Any<string>()).Returns("Host=localhost;Database=tenant_bus");
    var bus = Substitute.For<IMessageBus>();
    bus.InvokeAsync<Result<MigrateTenantDatabaseResult>>(
        Arg.Is<MigrateTenantDatabaseCommand>(c => c.TenantId == tenant.Id),
        Arg.Any<CancellationToken>(),
        Arg.Any<TimeSpan?>())
       .Returns(Result.Success(new MigrateTenantDatabaseResult(3)));

    var migrator = new TenantFleetMigrator(
        db,
        protector,
        normalizer,
        Options.Create(new MultiTenancyOptions()),
        Substitute.For<ILogger<TenantFleetMigrator>>(),
        bus: bus);

    var item = await migrator.MigrateTenantAsync(tenant.Id, CancellationToken.None);
    item.Succeeded.Should().BeTrue();
    item.AppliedMigrationsCount.Should().Be(3);
}
```

- [x] **Step 5: Run `TenantProvisionerAndFleetMigratorTests`**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~TenantProvisionerAndFleetMigratorTests"`
Expected: PASS

- [x] **Step 6: Commit Task 3**

```bash
git add apps/backend/
git commit -m "feat(tenants): extract ExamPrism.Modules.Tenants DLL and decouple provisioning via Wolverine messages"
```

---

### Task 4: Finalize Host (`ExamPrism`) Dynamic Plugin Wiring, Solution & Aspire Config, and Modular Monolith `ArchitectureTests`

**Files:**
- Move & Modify:
  - `apps/backend/src/ExamPrism.csproj` -> `apps/backend/src/ExamPrism/ExamPrism.csproj`
  - `apps/backend/src/Program.cs` -> `apps/backend/src/ExamPrism/Program.cs`
  - `apps/backend/src/appsettings.json`, `appsettings.Development.json` -> `apps/backend/src/ExamPrism/`
  - `apps/backend/src/Properties/launchSettings.json` -> `apps/backend/src/ExamPrism/Properties/launchSettings.json`
  - `apps/backend/src/Endpoints/Health/*`, `Endpoints/Test/*`, `Features/Health/*` -> `apps/backend/src/ExamPrism/`
- Modify: `apps/backend/ExamPrism.slnx`
- Modify: `aspire-app/AppHost.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/AspireAppHostConfigurationTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`

**Interfaces:**
- Consumes:
  - `ExamPrism.Contracts`, `ExamPrism.Infrastructure`, `ExamPrism.Modules.Tenants`, `ExamPrism.Modules.Identity`
- Produces:
  - `apps/backend/src/ExamPrism/ExamPrism.csproj` with dynamic plugin output copy target (`ReferenceOutputAssembly="false"`)
  - Updated `ArchitectureTests` enforcing module isolation, `IModule` implementation rules, and zero static Host-to-Module assembly references

- [x] **Step 1: Move Host files into `apps/backend/src/ExamPrism/` and configure MSBuild plugin output copying**

In `apps/backend/src/ExamPrism/ExamPrism.csproj`:
- Keep compile-time `<ProjectReference>` only to `..\ExamPrism.Contracts\ExamPrism.Contracts.csproj` and `..\ExamPrism.Infrastructure\ExamPrism.Infrastructure.csproj`.
- Add build-order-only references to module projects and copy their outputs (`*.dll`, `*.pdb`, `*.deps.json`, `appsettings.*.json`) into `$(OutDir)` after build:
```xml
<ItemGroup>
  <ProjectReference Include="..\ExamPrism.Contracts\ExamPrism.Contracts.csproj" />
  <ProjectReference Include="..\ExamPrism.Infrastructure\ExamPrism.Infrastructure.csproj" />
  <ProjectReference Include="..\Modules\Tenants\ExamPrism.Modules.Tenants.csproj"
                    ReferenceOutputAssembly="false"
                    CopyLocalSatelliteAssemblies="false" />
  <ProjectReference Include="..\Modules\Identity\ExamPrism.Modules.Identity.csproj"
                    ReferenceOutputAssembly="false"
                    CopyLocalSatelliteAssemblies="false" />
</ItemGroup>

<Target Name="CopyModulePluginOutputs" AfterTargets="Build">
  <ItemGroup>
    <ModulePluginFiles Include="..\Modules\*\bin\$(Configuration)\$(TargetFramework)\ExamPrism.Modules.*" />
    <ModuleSettingFiles Include="..\Modules\*\appsettings.*.json" />
  </ItemGroup>
  <Copy SourceFiles="@(ModulePluginFiles)" DestinationFolder="$(OutDir)" SkipUnchangedFiles="true" />
  <Copy SourceFiles="@(ModuleSettingFiles)" DestinationFolder="$(OutDir)" SkipUnchangedFiles="true" />
</Target>
```
Update `apps/backend/ExamPrism.slnx` to include all 5 `src` projects and `tests/ExamPrism.Tests/ExamPrism.Tests.csproj`. Update `aspire-app/AppHost.cs` and `AspireAppHostConfigurationTests.cs` path to `"../apps/backend/src/ExamPrism/ExamPrism.csproj"`.

- [x] **Step 2: Write failing Modular Monolith architecture tests in `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`**

Update `ArchitectureTests.AppAssemblies` to include `typeof(Program).Assembly`, `typeof(IModule).Assembly`, `typeof(DependencyInjection).Assembly`, `typeof(TenantsModule).Assembly`, and `typeof(IdentityModule).Assembly`, and add:
```csharp
[Fact]
public void Modules_MustImplementIModule_AndNotReferenceEachOther()
{
    var tenantsAssembly = typeof(TenantsModule).Assembly;
    var identityAssembly = typeof(IdentityModule).Assembly;

    tenantsAssembly.GetTypes().Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
        .Should().ContainSingle().Which.IsSealed.Should().BeTrue();

    identityAssembly.GetTypes().Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
        .Should().ContainSingle().Which.IsSealed.Should().BeTrue();

    tenantsAssembly.GetReferencedAssemblies().Select(a => a.Name)
        .Should().NotContain(identityAssembly.GetName().Name);

    identityAssembly.GetReferencedAssemblies().Select(a => a.Name)
        .Should().NotContain(tenantsAssembly.GetName().Name);
}

[Fact]
public void HostAssembly_MustNotHaveStaticAssemblyReferencesToModules()
{
    var referencedNames = typeof(Program).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
    referencedNames.Should().NotContain(n => n != null && n.StartsWith("ExamPrism.Modules.", StringComparison.Ordinal));
}
```

- [x] **Step 3: Run `ArchitectureTests` to verify all architecture rules pass**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter "FullyQualifiedName~ArchitectureTests"`
Expected: PASS

- [x] **Step 4: Run the full solution test suite to verify zero regressions**

Run: `dotnet test apps/backend/ExamPrism.slnx`
Expected: PASS (all unit, architecture, and integration tests pass)

- [x] **Step 5: Commit Task 4**

```bash
git add apps/backend/ aspire-app/
git commit -m "feat(host,tests): wire dynamic module loading in Host and enforce modular boundaries in ArchitectureTests"
```
