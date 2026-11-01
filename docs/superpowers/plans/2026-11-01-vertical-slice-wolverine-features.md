# Vertical Slice Architecture & Wolverine Full-Stack Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Transition ExamPrism from `Endpoints` minimal API reflection discovery to a Vertical Slice Architecture using Wolverine Full-Stack (`WolverineFx`, `WolverineFx.Http`, `WolverineFx.EntityFrameworkCore`), renaming `Endpoints` to `Features` via git mv while preserving git history.

**Architecture:** Vertical Slice Architecture where each slice in `apps/backend/src/Features/` encapsulates its requests, validators, models, and message/HTTP handlers. Handlers use a message-first pattern (named `*Handler` without `Endpoint` suffix) and are transport-agnostic, handling both HTTP routes and background/outbox messaging.

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core Minimal APIs / WolverineFx 4.8.0, WolverineFx.Http 4.8.0, WolverineFx.Http.FluentValidation 4.8.0, WolverineFx.EntityFrameworkCore 4.8.0, FluentValidation, Npgsql EF Core.

**Spec:** `docs/superpowers/specs/2026-11-01-vertical-slice-wolverine-features-design.md`

## Global Constraints

- Preserve git commit history: no rebase, no amend, no squashing of past commits.
- All file relocations must use `git mv`.
- Handler classes must not use the `*Endpoint` suffix (use `*Handler`).
- All validation must remain compatible with RFC 7807 ProblemDetails and existing localization resources (`SharedResources.resx`, `SharedResources.vi.resx`).
- Cookie handling for JWT authentication (`AuthCookieHelper`) must remain intact with `HttpOnly`, `SameSiteMode.Lax`, `SecurePolicy.SameAsRequest`.
- Central Package Management in `Directory.Packages.props` must be used for all NuGet package additions.

## Review Focus

1. **Case-insensitive health check routing**: `/health` vs `/Health` routing must continue to resolve cleanly.
2. **Missing or invalid cookies in Refresh/Logout**: `RefreshHandler` and `LogoutHandler` must gracefully handle missing refresh token cookie without 500 exceptions.
3. **Validation error format consistency**: Wolverine validation errors must match `ValidationProblemResponse` structure (`title`, `status: 400`, `errors` dictionary).
4. **Authorization enforcement on protected routes**: `GetMeHandler` and `LogoutHandler` must reject unauthenticated requests with 401 Unauthorized.
5. **Scalar/OpenAPI discovery**: Wolverine HTTP endpoints must be correctly discovered and exposed in OpenAPI document transformer.

---

### Task 1: Add Wolverine Packages to Central Package Management

**Files:**
- Modify: `apps/backend/Directory.Packages.props:1-29`
- Modify: `apps/backend/src/ExamPrism.csproj:1-26`

**Interfaces:**
- Consumes: None
- Produces: Package references for `WolverineFx`, `WolverineFx.Http`, `WolverineFx.Http.FluentValidation`, `WolverineFx.EntityFrameworkCore`

- [ ] **Step 1: Add package versions to `apps/backend/Directory.Packages.props`**

Add inside `<ItemGroup>`:
```xml
    <PackageVersion Include="WolverineFx" Version="4.8.0" />
    <PackageVersion Include="WolverineFx.Http" Version="4.8.0" />
    <PackageVersion Include="WolverineFx.Http.FluentValidation" Version="4.8.0" />
    <PackageVersion Include="WolverineFx.EntityFrameworkCore" Version="4.8.0" />
```

- [ ] **Step 2: Add PackageReferences to `apps/backend/src/ExamPrism.csproj`**

Add inside `<ItemGroup>`:
```xml
    <PackageReference Include="WolverineFx" />
    <PackageReference Include="WolverineFx.Http" />
    <PackageReference Include="WolverineFx.Http.FluentValidation" />
    <PackageReference Include="WolverineFx.EntityFrameworkCore" />
```

- [ ] **Step 3: Run dotnet restore / build to verify package installation**

Run: `dotnet build apps/backend/src/ExamPrism.csproj`
Expected: Build succeeds or downloads dependencies.

- [ ] **Step 4: Commit**

```bash
git add apps/backend/Directory.Packages.props apps/backend/src/ExamPrism.csproj
git commit -m "build(deps): add wolverine full-stack packages"
```

---

### Task 2: Rename `Endpoints` to `Features` via Git Move and Update Namespaces

**Files:**
- Move: `apps/backend/src/Endpoints/` -> `apps/backend/src/Features/`
- Modify: `apps/backend/src/Features/Auth/AuthCookieHelper.cs`
- Modify: `apps/backend/src/Features/HealthEndpoints.cs`
- Modify: `apps/backend/src/Features/Test/TestExceptionEndpoints.cs`
- Modify: `apps/backend/src/Features/Test/TestValidationEndpoints.cs`

**Interfaces:**
- Consumes: Existing endpoint files in `Endpoints/`
- Produces: Feature slices in `apps/backend/src/Features/` with namespace `ExamPrism.Features.*`

- [ ] **Step 1: Execute git mv from Endpoints to Features**

Run:
```bash
git mv apps/backend/src/Endpoints apps/backend/src/Features
```

- [ ] **Step 2: Update namespaces from `ExamPrism.Endpoints.*` to `ExamPrism.Features.*`**

Update:
- `apps/backend/src/Features/Auth/AuthCookieHelper.cs`: `namespace ExamPrism.Features.Auth;`
- `apps/backend/src/Features/HealthEndpoints.cs`: `namespace ExamPrism.Features.Health;`
- `apps/backend/src/Features/Test/TestExceptionEndpoints.cs`: `namespace ExamPrism.Features.Test;`
- `apps/backend/src/Features/Test/TestValidationEndpoints.cs`: `namespace ExamPrism.Features.Test;`

- [ ] **Step 3: Verify git status reflects pure file moves**

Run: `git status -s`
Expected: Renames tracked without deleted/untracked file duplicates.

- [ ] **Step 4: Commit**

```bash
git add apps/backend/src/Features
git commit -m "refactor(architecture): rename Endpoints directory to Features via git mv"
```

---

### Task 3: Refactor Auth Feature Slices to Message-First Wolverine Handlers

**Files:**
- Modify/Rename: `apps/backend/src/Features/Auth/LoginEndpoint.cs` -> `apps/backend/src/Features/Auth/LoginHandler.cs`
- Modify/Rename: `apps/backend/src/Features/Auth/RegisterEndpoint.cs` -> `apps/backend/src/Features/Auth/RegisterHandler.cs`
- Modify/Rename: `apps/backend/src/Features/Auth/RefreshEndpoint.cs` -> `apps/backend/src/Features/Auth/RefreshHandler.cs`
- Modify/Rename: `apps/backend/src/Features/Auth/LogoutEndpoint.cs` -> `apps/backend/src/Features/Auth/LogoutHandler.cs`
- Modify/Rename: `apps/backend/src/Features/Auth/GetMeEndpoint.cs` -> `apps/backend/src/Features/Auth/GetMeHandler.cs`

**Interfaces:**
- Consumes: `IAuthService`, `IUserContext`, `AuthCookieHelper`, `LoginRequest`, `RegisterRequest`
- Produces: `LoginHandler`, `RegisterHandler`, `RefreshHandler`, `LogoutHandler`, `GetMeHandler` in `ExamPrism.Features.Auth.*`

- [ ] **Step 1: Refactor `LoginHandler.cs`**

Rename file to `apps/backend/src/Features/Auth/LoginHandler.cs`.
Class `public static class LoginHandler`:
- Remove `IEndpoint` inheritance and `MapEndpoint` method.
- Add method:
```csharp
[WolverinePost("/api/auth/login")]
public static async Task<IResult> Handle(
    LoginRequest request,
    IAuthService authService,
    HttpResponse response,
    CancellationToken cancellationToken)
{
    var authResult = await authService.LoginAsync(request, cancellationToken);
    AuthCookieHelper.SetTokenCookies(response, authResult.AccessToken, authResult.RefreshToken);
    return Results.Ok(authResult.User);
}
```

- [ ] **Step 2: Refactor `RegisterHandler.cs`**

Rename file to `apps/backend/src/Features/Auth/RegisterHandler.cs`.
Class `public static class RegisterHandler`:
- Remove `IEndpoint` inheritance.
- Add method:
```csharp
[WolverinePost("/api/auth/register")]
public static async Task<IResult> Handle(
    RegisterRequest request,
    IAuthService authService,
    HttpResponse response,
    CancellationToken cancellationToken)
{
    var authResult = await authService.RegisterAsync(request, cancellationToken);
    AuthCookieHelper.SetTokenCookies(response, authResult.AccessToken, authResult.RefreshToken);
    return Results.Created("/api/auth/me", authResult.User);
}
```

- [ ] **Step 3: Refactor `RefreshHandler.cs`**

Rename file to `apps/backend/src/Features/Auth/RefreshHandler.cs`.
Class `public static class RefreshHandler`:
- Remove `IEndpoint` inheritance.
- Add method:
```csharp
[WolverinePost("/api/auth/refresh")]
public static async Task<IResult> Handle(
    HttpRequest request,
    HttpResponse response,
    IAuthService authService,
    CancellationToken cancellationToken)
{
    var refreshToken = AuthCookieHelper.GetRefreshToken(request);
    if (string.IsNullOrWhiteSpace(refreshToken))
    {
        return Results.Unauthorized();
    }

    var authResult = await authService.RefreshTokenAsync(refreshToken, cancellationToken);
    if (authResult is null)
    {
        AuthCookieHelper.ClearTokenCookies(response);
        return Results.Unauthorized();
    }

    AuthCookieHelper.SetTokenCookies(response, authResult.AccessToken, authResult.RefreshToken);
    return Results.Ok(new RefreshTokenResponse(authResult.AccessToken));
}
```

- [ ] **Step 4: Refactor `LogoutHandler.cs` and `GetMeHandler.cs`**

Rename `LogoutEndpoint.cs` to `LogoutHandler.cs`:
```csharp
[WolverinePost("/api/auth/logout")]
[Authorize]
public static async Task<IResult> Handle(
    HttpRequest request,
    HttpResponse response,
    IAuthService authService,
    CancellationToken cancellationToken)
{
    var refreshToken = AuthCookieHelper.GetRefreshToken(request);
    if (!string.IsNullOrWhiteSpace(refreshToken))
    {
        await authService.RevokeTokenAsync(refreshToken, cancellationToken);
    }
    AuthCookieHelper.ClearTokenCookies(response);
    return Results.NoContent();
}
```

Rename `GetMeEndpoint.cs` to `GetMeHandler.cs`:
```csharp
[WolverineGet("/api/auth/me")]
[Authorize]
public static async Task<IResult> Handle(
    IUserContext userContext,
    IAuthService authService,
    CancellationToken cancellationToken)
{
    if (userContext.UserId is null)
    {
        return Results.Unauthorized();
    }
    var user = await authService.GetUserByIdAsync(userContext.UserId.Value, cancellationToken);
    return user is not null ? Results.Ok(user) : Results.NotFound();
}
```

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Features/Auth/
git commit -m "feat(auth): refactor auth slices to wolverine handlers"
```

---

### Task 4: Refactor Health and Test Feature Slices

**Files:**
- Modify/Rename: `apps/backend/src/Features/HealthEndpoints.cs` -> `apps/backend/src/Features/Health/HealthHandler.cs`
- Modify/Rename: `apps/backend/src/Features/Test/TestExceptionEndpoints.cs` -> `apps/backend/src/Features/Test/TestExceptionHandlers.cs`
- Modify/Rename: `apps/backend/src/Features/Test/TestValidationEndpoints.cs` -> `apps/backend/src/Features/Test/TestValidationHandlers.cs`

**Interfaces:**
- Consumes: Test models and exception types
- Produces: `HealthHandler`, `TestExceptionHandlers`, `TestValidationHandlers`

- [ ] **Step 1: Refactor `HealthHandler.cs`**

Move to `apps/backend/src/Features/Health/HealthHandler.cs`:
```csharp
namespace ExamPrism.Features.Health;

using Wolverine.Http;

public static class HealthHandler
{
    [WolverineGet("/health")]
    public static IResult Handle() => Results.Ok(new { status = "Healthy" });
}
```

- [ ] **Step 2: Refactor `TestExceptionHandlers.cs` and `TestValidationHandlers.cs`**

Remove `IEndpoint` implementations.
In `TestExceptionHandlers.cs`:
- `[WolverineGet("/api/test/exceptions/not-found")]`: throws `NotFoundException`
- `[WolverineGet("/api/test/exceptions/domain")]`: throws `DomainException`
- `[WolverineGet("/api/test/exceptions/unauthorized")]`: throws `UnauthorizedAccessException`

In `TestValidationHandlers.cs`:
- `[WolverinePost("/api/test/validation/test-model")]`: validates `TestValidationModel` with FluentValidation.

- [ ] **Step 3: Commit**

```bash
git add apps/backend/src/Features/Health apps/backend/src/Features/Test
git commit -m "feat(features): refactor health and test slices to wolverine handlers"
```

---

### Task 5: Configure Program.cs and Remove Obsolete `Common/Endpoints`

**Files:**
- Modify: `apps/backend/src/Program.cs:1-64`
- Delete: `apps/backend/src/Common/Endpoints/IEndpoint.cs`
- Delete: `apps/backend/src/Common/Endpoints/EndpointExtensions.cs`

**Interfaces:**
- Consumes: Wolverine host builder & endpoint routing
- Produces: Clean pipeline with `UseWolverine` and `MapWolverineEndpoints`

- [ ] **Step 1: Configure Wolverine in `apps/backend/src/Program.cs`**

- Remove: `using Common.Endpoints;`
- Add:
```csharp
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Http;
using Wolverine.Http.FluentValidation;
```
- Configure Wolverine host:
```csharp
builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
});
```
- Configure HTTP endpoints:
Replace `app.MapEndpoints();` with:
```csharp
app.MapWolverineEndpoints(opts =>
{
    opts.UseFluentValidationProblemDetailMiddleware();
});
```

- [ ] **Step 2: Delete `apps/backend/src/Common/Endpoints/`**

Run:
```bash
git rm apps/backend/src/Common/Endpoints/IEndpoint.cs apps/backend/src/Common/Endpoints/EndpointExtensions.cs
```

- [ ] **Step 3: Verify build passes with Wolverine configuration**

Run: `dotnet build apps/backend/src/ExamPrism.csproj`
Expected: Build succeeds with 0 errors.

- [ ] **Step 4: Commit**

```bash
git add apps/backend/src/Program.cs apps/backend/src/Common/Endpoints
git commit -m "feat(wolverine): configure wolverine pipeline and remove legacy IEndpoint discovery"
```

---

### Task 6: Update Test Suite and Verify Architecture

**Files:**
- Delete: `apps/backend/tests/ExamPrism.Tests/EndpointExtensionsTests.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Features/WolverineHandlerTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Endpoints/AuthEndpointTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Endpoints/AuthCookieHelperTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/HealthEndpointTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Endpoints/ScalarApiDocsTests.cs`

**Interfaces:**
- Consumes: Wolverine endpoint pipeline in `CustomWebApplicationFactory`
- Produces: Passing unit & integration test suite

- [ ] **Step 1: Remove obsolete `EndpointExtensionsTests.cs` and add `WolverineHandlerTests.cs`**

Run:
```bash
git rm apps/backend/tests/ExamPrism.Tests/EndpointExtensionsTests.cs
```
Create `WolverineHandlerTests.cs` testing static handler methods directly (e.g. `LoginHandler.Handle` assertions with mocked services).

- [ ] **Step 2: Update test namespaces and route expectations**

Update imports in test files from `ExamPrism.Endpoints.*` to `ExamPrism.Features.*`.
Move test files to match `Features/` structure:
- `tests/ExamPrism.Tests/Endpoints/` -> `tests/ExamPrism.Tests/Features/`

- [ ] **Step 3: Run unit tests**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName!~Integration"`
Expected: All unit tests pass.

- [ ] **Step 4: Verify full project build**

Run: `dotnet build apps/backend/ExamPrism.slnx`
Expected: Build succeeds with 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/tests/ExamPrism.Tests/
git commit -m "test(features): update test suite for vertical slices and wolverine handlers"
```
