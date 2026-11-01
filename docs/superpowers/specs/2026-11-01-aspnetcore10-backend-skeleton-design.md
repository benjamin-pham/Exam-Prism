# Design Spec: ASP.NET Core 10 Backend Skeleton

- **Date:** 2026-11-01
- **Status:** Proposed
- **Target Path:** `apps/backend/`

## 1. Overview & Goals

Initialize the initial ASP.NET Core 10 Web API backend inside `apps/backend/` for the Exam-Prism project.
The goal is to provide a clean, modern, single-project Minimal API foundation with testing infrastructure, ready for feature development without premature architectural complexity (YAGNI).

### Key Goals:
- Scaffold ASP.NET Core 10 Web API targeting `.NET 10.0` directly inside `apps/backend/src`.
- Project naming and root namespace: `ExamPrism.Api`.
- Use Minimal API pattern with endpoint grouping extension methods.
- Provide built-in health check endpoint (`/health`).
- Enable OpenAPI / Swagger support for API exploration.
- Scaffold an accompanying integration/unit test project using `xUnit` and `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory`) under `apps/backend/tests/ExamPrism.Api.Tests`.
- Add a modern solution file (`apps/backend/ExamPrism.slnx` or `.sln`) linking both projects.
- Verify complete compilation and test execution via `dotnet build` and `dotnet test`.

---

## 2. Project Architecture & Directory Layout

```text
apps/backend/
├── ExamPrism.slnx
├── src/
│   ├── ExamPrism.Api.csproj
│   ├── Program.cs
│   ├── Endpoints/
│   │   └── HealthEndpoints.cs
│   ├── Properties/
│   │   └── launchSettings.json
│   ├── appsettings.json
│   └── appsettings.Development.json
└── tests/
    └── ExamPrism.Api.Tests/
        ├── ExamPrism.Api.Tests.csproj
        ├── HealthEndpointTests.cs
        └── Usings.cs
```

---

## 3. Detailed Component Specifications

### 3.1 Solution File
- File: `apps/backend/ExamPrism.slnx` (or `ExamPrism.sln` if `slnx` is not supported by installed tooling, fallback handled cleanly).
- Contains projects:
  - `src/ExamPrism.Api.csproj`
  - `tests/ExamPrism.Api.Tests/ExamPrism.Api.Tests.csproj`

### 3.2 API Project (`apps/backend/src/ExamPrism.Api.csproj`)
- Target Framework: `net10.0`
- Nullable: `enable`
- ImplicitUsings: `enable`
- Properties:
  - Project name: `ExamPrism.Api`
  - Root namespace: `ExamPrism.Api`
- Dependencies:
  - `Microsoft.AspNetCore.OpenApi` (net10.0)
- Expose `Program` class internally to `ExamPrism.Api.Tests` via `[InternalsVisibleTo]` or public partial class for `WebApplicationFactory`.

### 3.3 Program & Endpoints
- `Program.cs`:
  - Configures services: OpenAPI (`AddOpenApi()`), HealthChecks (`AddHealthChecks()`), ProblemDetails.
  - Development middleware: OpenAPI endpoint mapping (`MapOpenApi()`).
  - Routing: calls `app.MapHealthEndpoints()`.
  - Exposes `public partial class Program { }` at the end for test fixture access.
- `Endpoints/HealthEndpoints.cs`:
  - Static class `HealthEndpoints` with extension method:
    ```csharp
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    ```
  - Maps `GET /health` returning status HTTP 200 with `{ "status": "Healthy", "timestamp": "..." }`.

### 3.4 Configuration Files
- `appsettings.json`:
  - Standard logging levels (`Information`, `Microsoft.AspNetCore: Warning`).
  - AllowedHosts: `*`.
- `appsettings.Development.json`:
  - Verbose logging (`Debug`).
- `Properties/launchSettings.json`:
  - HTTP profile on `http://localhost:5000` (or `http://localhost:5100`).
  - HTTPS optional / disabled initially in container/dev mode to prevent SSL cert prompt friction unless requested.

### 3.5 Test Project (`apps/backend/tests/ExamPrism.Api.Tests/`)
- Target Framework: `net10.0`
- Packages:
  - `Microsoft.AspNetCore.Mvc.Testing`
  - `Microsoft.NET.Test.Sdk`
  - `xUnit`
  - `xunit.runner.visualstudio`
  - `coverlet.collector`
  - `FluentAssertions` (optional/standard)
- Project Reference:
  - `../../src/ExamPrism.Api.csproj`
- Test Case (`HealthEndpointTests.cs`):
  - Uses `WebApplicationFactory<Program>`.
  - Sends `GET /health`.
  - Asserts HTTP 200 OK and response contains `"Healthy"`.

---

## 4. Verification & Success Criteria

1. `dotnet build apps/backend/ExamPrism.sln*` succeeds with 0 errors and 0 warnings.
2. `dotnet test apps/backend/ExamPrism.sln*` runs all tests and passes.
3. Clean directory structure with no temporary boilerplate or unused files.
4. Git commit tracking the scaffolded backend structure.

