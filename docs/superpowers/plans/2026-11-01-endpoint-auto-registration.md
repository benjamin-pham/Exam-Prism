# Endpoint Auto-Registration (`IEndpoint`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide an `IEndpoint` interface with `static abstract void MapEndpoint(IEndpointRouteBuilder app)` and an automated discovery extension `MapEndpoints` that scans assemblies and maps all endpoints at startup with zero object allocations.

**Architecture:** A static abstract interface `IEndpoint` residing in `Common.Endpoints` and an extension method `MapEndpoints` on `IEndpointRouteBuilder` that scans assemblies for non-abstract, non-interface classes implementing `IEndpoint` and invokes their static `MapEndpoint` method.

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core Minimal APIs, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-01-endpoint-auto-registration-design.md`

## Global Constraints

- Target Framework: .NET 10 (`net10.0`).
- No additional third-party NuGet packages; rely on standard .NET 10 reflection and ASP.NET Core routing abstractions.
- Central Package Management enabled via `Directory.Packages.props`.
- All commands in Linux environment must run with `XDG_RUNTIME_DIR=/tmp TMPDIR=/tmp` when executing `dotnet` commands.

## Review Focus

1. Explicit interface implementation (`static void IEndpoint.MapEndpoint`): Reflection must find and invoke the method even when not declared as public static on the class.
2. Abstract classes implementing `IEndpoint`: Scanner must ignore abstract classes and never attempt invocation.
3. Assembly throwing `ReflectionTypeLoadException`: Scanner must safely collect loaded types and continue without crashing.
4. Empty assemblies argument: `app.MapEndpoints()` without parameters must fall back to EntryAssembly / ExecutingAssembly.
5. Distinct type mapping: Scanner handles duplicate assembly arguments gracefully without executing identical type maps multiple times.

---

### Task 1: Core Abstraction & Discovery Extension (`IEndpoint` & `EndpointExtensions`)

**Files:**
- Create: `apps/backend/src/Common/Endpoints/IEndpoint.cs`
- Create: `apps/backend/src/Common/Endpoints/EndpointExtensions.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/EndpointExtensionsTests.cs`

**Interfaces:**
- Produces:
  - `Common.Endpoints.IEndpoint`:
    ```csharp
    public interface IEndpoint
    {
        static abstract void MapEndpoint(IEndpointRouteBuilder app);
    }
    ```
  - `Common.Endpoints.EndpointExtensions`:
    ```csharp
    public static class EndpointExtensions
    {
        public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder app, params Assembly[] assemblies);
        public static IEndpointRouteBuilder MapEndpoints<TMarker>(this IEndpointRouteBuilder app);
    }
    ```

- [ ] **Step 1: Write the failing unit tests for `EndpointExtensions`**

Create `apps/backend/tests/ExamPrism.Tests/EndpointExtensionsTests.cs` containing:
- Test endpoint class implementing `IEndpoint` with a tracking boolean flag.
- Test endpoint class using explicit interface implementation (`static void IEndpoint.MapEndpoint`).
- Test abstract class implementing `IEndpoint` (must not throw or be called).
- Test class not implementing `IEndpoint` (ignored).
- Test verifying `app.MapEndpoints(assembly)` discovers and invokes the test endpoints.
- Test verifying `app.MapEndpoints<TMarker>()` discovers and invokes the test endpoints.

```csharp
namespace Tests;

using System.Reflection;
using Common.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Xunit;

public class EndpointExtensionsTests
{
    public class MockEndpointRouteBuilder : IEndpointRouteBuilder
    {
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotImplementedException();
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IServiceProvider ServiceProvider => null!;
    }

    public class StandardTestEndpoint : IEndpoint
    {
        public static bool Invoked { get; set; }
        public static void MapEndpoint(IEndpointRouteBuilder app) => Invoked = true;
    }

    public class ExplicitTestEndpoint : IEndpoint
    {
        public static bool Invoked { get; set; }
        static void IEndpoint.MapEndpoint(IEndpointRouteBuilder app) => Invoked = true;
    }

    public abstract class AbstractTestEndpoint : IEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app) => throw new InvalidOperationException("Should not be called");
    }

    public class NonEndpoint
    {
        public static void MapEndpoint(IEndpointRouteBuilder app) => throw new InvalidOperationException("Should not be called");
    }

    [Fact]
    public void MapEndpoints_InvokesRegisteredEndpoints()
    {
        StandardTestEndpoint.Invoked = false;
        ExplicitTestEndpoint.Invoked = false;
        var builder = new MockEndpointRouteBuilder();

        builder.MapEndpoints(typeof(EndpointExtensionsTests).Assembly);

        Assert.True(StandardTestEndpoint.Invoked);
        Assert.True(ExplicitTestEndpoint.Invoked);
    }

    [Fact]
    public void MapEndpoints_GenericMarker_InvokesRegisteredEndpoints()
    {
        StandardTestEndpoint.Invoked = false;
        var builder = new MockEndpointRouteBuilder();

        builder.MapEndpoints<EndpointExtensionsTests>();

        Assert.True(StandardTestEndpoint.Invoked);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run:
```bash
XDG_RUNTIME_DIR=/tmp TMPDIR=/tmp dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter FullyQualifiedName~EndpointExtensionsTests
```
Expected: FAIL with compilation error (CS0246: `The type or namespace name 'Common' could not be found`).

- [ ] **Step 3: Implement `IEndpoint` and `EndpointExtensions`**

Create `apps/backend/src/Common/Endpoints/IEndpoint.cs`:
```csharp
namespace Common.Endpoints;

using Microsoft.AspNetCore.Routing;

public interface IEndpoint
{
    static abstract void MapEndpoint(IEndpointRouteBuilder app);
}
```

Create `apps/backend/src/Common/Endpoints/EndpointExtensions.cs`:
```csharp
namespace Common.Endpoints;

using System.Reflection;
using Microsoft.AspNetCore.Routing;

public static class EndpointExtensions
{
    public static IEndpointRouteBuilder MapEndpoints(
        this IEndpointRouteBuilder app,
        params Assembly[] assemblies)
    {
        var targetAssemblies = assemblies.Length > 0
            ? assemblies.Distinct()
            : [Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()];

        var endpointTypes = targetAssemblies
            .SelectMany(GetExportedTypesSafely)
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IEndpoint).IsAssignableFrom(t))
            .Distinct();

        foreach (var type in endpointTypes)
        {
            var mapMethod = type.GetMethod(
                nameof(IEndpoint.MapEndpoint),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                [typeof(IEndpointRouteBuilder)])
                ?? type.GetInterfaceMap(typeof(IEndpoint)).TargetMethods.FirstOrDefault();

            mapMethod?.Invoke(null, [app]);
        }

        return app;
    }

    public static IEndpointRouteBuilder MapEndpoints<TMarker>(this IEndpointRouteBuilder app)
        => app.MapEndpoints(typeof(TMarker).Assembly);

    private static IEnumerable<Type> GetExportedTypesSafely(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run:
```bash
XDG_RUNTIME_DIR=/tmp TMPDIR=/tmp dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter FullyQualifiedName~EndpointExtensionsTests
```
Expected: PASS (all 2 tests passed).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Endpoints/IEndpoint.cs apps/backend/src/Common/Endpoints/EndpointExtensions.cs apps/backend/tests/ExamPrism.Tests/EndpointExtensionsTests.cs
git commit -m "feat(common): add IEndpoint interface and automated MapEndpoints discovery"
```

---

### Task 2: Migrate `HealthEndpoints` & Integrate into `Program.cs`

**Files:**
- Modify: `apps/backend/src/Endpoints/HealthEndpoints.cs`
- Modify: `apps/backend/src/Program.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/HealthEndpointTests.cs`

**Interfaces:**
- Consumes:
  - `Common.Endpoints.IEndpoint`
  - `Common.Endpoints.EndpointExtensions.MapEndpoints`
- Produces:
  - `Endpoints.HealthEndpoints : IEndpoint`: `public static void MapEndpoint(IEndpointRouteBuilder app)`

- [ ] **Step 1: Verify existing regression tests**

Review `apps/backend/tests/ExamPrism.Tests/HealthEndpointTests.cs`:
- Tests `GetHealth_HandlesCaseVariations` (`/health`, `/Health`, `/HEALTH`)
- Tests `GetOpenApi_ReturnsOk_InDevelopment` (`/openapi/v1.json`)

- [ ] **Step 2: Migrate `HealthEndpoints.cs` to implement `IEndpoint`**

Update `apps/backend/src/Endpoints/HealthEndpoints.cs`:
```csharp
namespace Endpoints;

using Common.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public record HealthResponse(string Status, DateTime Timestamp);

public class HealthEndpoints : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new HealthResponse("Healthy", DateTime.UtcNow)))
            .WithName("GetHealth")
            .WithDescription("Returns health status of the API");
    }
}
```

- [ ] **Step 3: Update `Program.cs` to call `app.MapEndpoints()`**

Update `apps/backend/src/Program.cs`:
Replace `using Endpoints;` and `app.MapHealthEndpoints();` with `using Common.Endpoints;` and `app.MapEndpoints();`.

```csharp
using Common.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapEndpoints();

app.Run();

public partial class Program { }
```

- [ ] **Step 4: Run full test suite to verify regression and integration**

Run:
```bash
XDG_RUNTIME_DIR=/tmp TMPDIR=/tmp dotnet test apps/backend/ExamPrism.slnx
```
Expected: PASS (all tests pass, including `HealthEndpointTests` and `EndpointExtensionsTests`).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Endpoints/HealthEndpoints.cs apps/backend/src/Program.cs
git commit -m "feat(api): migrate HealthEndpoints to IEndpoint and wire MapEndpoints in Program.cs"
```
