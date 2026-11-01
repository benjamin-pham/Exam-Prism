# Design Document: Automated Endpoint Discovery & Registration (`IEndpoint`)

- **Author**: Antigravity Assistant & Engineering Team
- **Date**: 2026-11-01
- **Status**: Draft (Approved in Brainstorming)
- **Target Framework**: .NET 10 (`net10.0`), C# 13

---

## 1. Overview & Motivation

ExamPrism's backend currently requires manual route mapping for each endpoint group (e.g. `app.MapHealthEndpoints()` in `Program.cs`). As the application grows to support more feature verticals, manual endpoint registration in `Program.cs` becomes bloated, prone to merge conflicts, and error-prone when newly created endpoints are forgotten.

This design introduces a unified endpoint abstraction `IEndpoint` leveraging C# static abstract interface methods (`static abstract void MapEndpoint(IEndpointRouteBuilder app)`) and an automated discovery extension `MapEndpoints(...)` to register all endpoints at startup with zero object allocations and minimal boilerplate.

---

## 2. Architecture & Design Decisions

### 2.1 Static Abstract Interface (`IEndpoint`)
- **Location**: `apps/backend/src/Common/Endpoints/IEndpoint.cs`
- **Namespace**: `Common.Endpoints`
- **Pattern**: Static Abstract Interface Member (C# 11+ / .NET 10).
- **Rationale**: Minimal API route registration does not require holding state or allocating an endpoint instance per route definition. By defining `MapEndpoint` as a static abstract method, endpoint classes define static mapping logic without requiring dependency injection container registration or runtime `Activator.CreateInstance` calls.

```csharp
namespace Common.Endpoints;

using Microsoft.AspNetCore.Routing;

/// <summary>
/// Defines an endpoint or endpoint group mapped into ASP.NET Core Minimal API routing.
/// Uses static abstract methods to eliminate object allocations during startup registration.
/// </summary>
public interface IEndpoint
{
    static abstract void MapEndpoint(IEndpointRouteBuilder app);
}
```

### 2.2 Endpoint Discovery Extension (`EndpointExtensions`)
- **Location**: `apps/backend/src/Common/Endpoints/EndpointExtensions.cs`
- **Namespace**: `Common.Endpoints`
- **Behavior**:
  - `MapEndpoints(this IEndpointRouteBuilder app, params Assembly[] assemblies)`:
    - Defaults to `Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()` when no assemblies are explicitly passed.
    - Scans exported types safely using `ReflectionTypeLoadException` handling.
    - Filters for non-abstract, non-interface types implementing `IEndpoint`.
    - Locates and invokes `MapEndpoint(app)` handling both standard public static methods and explicit static interface implementations.
  - `MapEndpoints<TMarker>(this IEndpointRouteBuilder app)`:
    - Convenience overload scanning the assembly where `TMarker` resides.

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
            ? assemblies
            : [Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()];

        var endpointTypes = targetAssemblies
            .SelectMany(GetExportedTypesSafely)
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IEndpoint).IsAssignableFrom(t));

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

---

## 3. Migration & Integration

### 3.1 Migration of Existing `HealthEndpoints`
- Update `apps/backend/src/Endpoints/HealthEndpoints.cs` to implement `IEndpoint`:
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

### 3.2 Update `Program.cs`
- Replace `app.MapHealthEndpoints()` with `app.MapEndpoints()`:
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

---

## 4. Error Handling & Edge Cases

1. **Missing or Corrupted Assembly Types**: `GetExportedTypesSafely` catches `ReflectionTypeLoadException` so unresolvable optional dependencies in scanned assemblies do not crash startup.
2. **Explicit Interface Implementations**: Reflection checks both `BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static` and falls back to `type.GetInterfaceMap(typeof(IEndpoint))` to guarantee execution even if an endpoint explicitly implements `static void IEndpoint.MapEndpoint`.
3. **Abstract Base Classes**: Abstract classes implementing `IEndpoint` (e.g. for sharing helper routing utilities) are explicitly filtered out (`!t.IsAbstract`).

---

## 5. Testing & Verification Plan

### 5.1 Existing Regression Tests
- `apps/backend/tests/ExamPrism.Tests/HealthEndpointTests.cs`:
  - `GetHealth_HandlesCaseVariations` (`/health`, `/Health`, `/HEALTH`) must pass.
  - `GetOpenApi_ReturnsOk_InDevelopment` must pass.

### 5.2 Unit Tests for Discovery Logic
- Add `apps/backend/tests/ExamPrism.Tests/EndpointExtensionsTests.cs`:
  - Verify that `MapEndpoints()` scans and executes `MapEndpoint` on valid `IEndpoint` implementations.
  - Verify that abstract classes and non-endpoint classes are ignored without errors.
  - Verify that `MapEndpoints<TMarker>()` and custom assembly arrays work properly.

---

## 6. Files Impacted

| File | Action | Purpose |
|------|--------|---------|
| `apps/backend/src/Common/Endpoints/IEndpoint.cs` | Create | Interface contract with static abstract method |
| `apps/backend/src/Common/Endpoints/EndpointExtensions.cs` | Create | Auto-discovery and registration extension methods |
| `apps/backend/src/Endpoints/HealthEndpoints.cs` | Modify | Migrate to implement `IEndpoint` |
| `apps/backend/src/Program.cs` | Modify | Replace manual call with `app.MapEndpoints()` |
| `apps/backend/tests/ExamPrism.Tests/EndpointExtensionsTests.cs` | Create | Unit tests for assembly scanning and route mapping |
