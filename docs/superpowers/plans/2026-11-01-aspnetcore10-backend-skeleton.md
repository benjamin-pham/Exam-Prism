# ASP.NET Core 10 Backend Skeleton Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Initialize a clean, modern ASP.NET Core 10 Minimal API project in `apps/backend/src` with a companion test project in `apps/backend/tests` and an integrated solution file.

**Architecture:** Single-project Minimal API with endpoint mapping extension methods (`MapHealthEndpoints`), built-in OpenAPI and health check services, and an integration test project utilizing `WebApplicationFactory<Program>` to test endpoints end-to-end.

**Tech Stack:** .NET 10.0, ASP.NET Core 10, C# 14, xUnit, Microsoft.AspNetCore.Mvc.Testing, Microsoft.AspNetCore.OpenApi.

**Spec:** `docs/superpowers/specs/2026-11-01-aspnetcore10-backend-skeleton-design.md`

## Global Constraints

- Root target framework must be `net10.0`.
- All commands in sandboxed Linux environment must include environment variables `XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp/dotnet`.
- Project naming and root namespace for the API must be `ExamPrism.Api`.
- API source code resides directly in `apps/backend/src/`.
- Test project resides in `apps/backend/tests/ExamPrism.Api.Tests/`.
- Solution file is `apps/backend/ExamPrism.sln`.
- Nullable reference types and ImplicitUsings enabled.

## Review Focus

1. **Endpoint casing and path format:** Requesting `/health` must return HTTP 200 JSON with status "Healthy", and case variations like `/Health` should resolve properly via ASP.NET Core default routing.
2. **Missing Program visibility:** `WebApplicationFactory<Program>` in the test project requires `Program` in `ExamPrism.Api` to be accessible (`public partial class Program { }`).
3. **OpenAPI endpoint routing:** In development environment (`ASPNETCORE_ENVIRONMENT=Development`), OpenAPI metadata must be mapped without crashing when run headless.
4. **Clean clean/build/test lifecycle:** `dotnet build` and `dotnet test` on the root solution must succeed with zero warnings and zero failures.
5. **Port conflicts during launch:** Launch settings should configure predictable local ports (e.g., `http://localhost:5000` / `https://localhost:5001`) with clear development profiles.

---

### Task 1: Initialize Backend Solution & API Project Scaffolding

**Files:**
- Create: `apps/backend/ExamPrism.sln`
- Create: `apps/backend/src/ExamPrism.Api.csproj`
- Create: `apps/backend/src/Properties/launchSettings.json`
- Create: `apps/backend/src/appsettings.json`
- Create: `apps/backend/src/appsettings.Development.json`

**Interfaces:**
- Consumes: None (initial setup)
- Produces: `apps/backend/ExamPrism.sln` and `apps/backend/src/ExamPrism.Api.csproj` targeting `net10.0`

- [ ] **Step 1: Create solution file and API project directory structure**

Run:
```bash
XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp/dotnet dotnet new sln -n ExamPrism -o apps/backend
```
Expected: `The template "Solution File" was created successfully.` at `apps/backend/ExamPrism.sln`.

- [ ] **Step 2: Create `apps/backend/src/ExamPrism.Api.csproj`**

Write `apps/backend/src/ExamPrism.Api.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>ExamPrism.Api</RootNamespace>
    <AssemblyName>ExamPrism.Api</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" Version="10.0.*" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Add configuration files (`appsettings.json`, `appsettings.Development.json`, and `launchSettings.json`)**

Write `apps/backend/src/appsettings.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

Write `apps/backend/src/appsettings.Development.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Information"
    }
  }
}
```

Write `apps/backend/src/Properties/launchSettings.json`:
```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "http": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": false,
      "applicationUrl": "http://localhost:5000",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

- [ ] **Step 4: Register API project in solution and verify build**

Run:
```bash
XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp/dotnet dotnet sln apps/backend/ExamPrism.sln add apps/backend/src/ExamPrism.Api.csproj
```
Expected: `Project `apps/backend/src/ExamPrism.Api.csproj` added to the solution.`

- [ ] **Step 5: Commit API scaffolding**

Run:
```bash
git add apps/backend/ExamPrism.sln apps/backend/src/
git commit -m "feat(backend): scaffold initial solution and ExamPrism.Api project"
```

---

### Task 2: Health Endpoints, Program Entrypoint & Integration Tests

**Files:**
- Create: `apps/backend/tests/ExamPrism.Api.Tests/ExamPrism.Api.Tests.csproj`
- Create: `apps/backend/tests/ExamPrism.Api.Tests/HealthEndpointTests.cs`
- Create: `apps/backend/src/Endpoints/HealthEndpoints.cs`
- Create: `apps/backend/src/Program.cs`
- Modify: `apps/backend/ExamPrism.sln`

**Interfaces:**
- Consumes: `ExamPrism.Api.csproj`
- Produces: `HealthEndpoints.MapHealthEndpoints(this IEndpointRouteBuilder app)`, `public partial class Program { }`, `HealthEndpointTests` validating `GET /health`

- [ ] **Step 1: Create test project `ExamPrism.Api.Tests` and link to solution**

Write `apps/backend/tests/ExamPrism.Api.Tests/ExamPrism.Api.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.*">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.*" />
    <PackageReference Include="xunit" Version="2.9.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.0.*">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\ExamPrism.Api.csproj" />
  </ItemGroup>

</Project>
```

Add test project to solution:
```bash
XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp/dotnet dotnet sln apps/backend/ExamPrism.sln add apps/backend/tests/ExamPrism.Api.Tests/ExamPrism.Api.Tests.csproj
```

- [ ] **Step 2: Write failing test in `HealthEndpointTests.cs`**

Write `apps/backend/tests/ExamPrism.Api.Tests/HealthEndpointTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ExamPrism.Api.Tests;

public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetHealth_ReturnsOk_WithHealthyStatus()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(content);
        Assert.Equal("Healthy", content.Status);
    }

    private record HealthResponse(string Status, DateTime Timestamp);
}
```

- [ ] **Step 3: Run test to verify it fails (compilation failure because `Program` does not exist yet)**

Run:
```bash
XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp/dotnet dotnet test apps/backend/tests/ExamPrism.Api.Tests/ExamPrism.Api.Tests.csproj
```
Expected: FAIL due to missing `Program` class.

- [ ] **Step 4: Implement `HealthEndpoints.cs` and `Program.cs`**

Write `apps/backend/src/Endpoints/HealthEndpoints.cs`:
```csharp
namespace ExamPrism.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new
        {
            Status = "Healthy",
            Timestamp = DateTime.UtcNow
        }))
        .WithName("GetHealth")
        .WithDescription("Returns health status of the API")
        .WithOpenApi();

        return app;
    }
}
```

Write `apps/backend/src/Program.cs`:
```csharp
using ExamPrism.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthEndpoints();

app.Run();

public partial class Program { }
```

- [ ] **Step 5: Run tests and verify they pass**

Run:
```bash
XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp/dotnet dotnet test apps/backend/tests/ExamPrism.Api.Tests/ExamPrism.Api.Tests.csproj
```
Expected: PASS with 1 passed test.

- [ ] **Step 6: Commit health endpoint implementation and tests**

Run:
```bash
git add apps/backend/
git commit -m "feat(backend): add health endpoint and integration test suite"
```

---

### Task 3: Complete Solution Verification & Documentation

**Files:**
- Modify: `.gitignore` (if necessary, ensure bin/obj directories are ignored)
- Test: All projects in `apps/backend/ExamPrism.sln`

**Interfaces:**
- Consumes: Task 1 and Task 2 deliverables
- Produces: Clean, passing solution build and test execution

- [ ] **Step 1: Check `.gitignore` for standard .NET ignore rules**

Inspect `.gitignore`. If `bin/` and `obj/` are not ignored, append:
```gitignore
# .NET
bin/
obj/
TestResults/
```

- [ ] **Step 2: Run full clean, build, and test from solution root**

Run:
```bash
XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp/dotnet dotnet clean apps/backend/ExamPrism.sln
XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp/dotnet dotnet build apps/backend/ExamPrism.sln --no-incremental
XDG_RUNTIME_DIR=/tmp DOTNET_CLI_HOME=/tmp/dotnet dotnet test apps/backend/ExamPrism.sln --logger "console;verbosity=normal"
```
Expected: Build succeeded with 0 warnings and 0 errors. All tests passed.

- [ ] **Step 3: Commit final verification and any gitignore updates**

Run:
```bash
git add .gitignore
git commit -m "chore(backend): ensure .NET build artifacts are gitignored"
```
