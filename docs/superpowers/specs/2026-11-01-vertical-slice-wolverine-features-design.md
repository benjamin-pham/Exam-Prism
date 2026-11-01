# Design Document: Vertical Slice Architecture & Wolverine Full-Stack Integration

- **Author**: Antigravity Assistant & Engineering Team
- **Date**: 2026-11-01
- **Status**: Approved (Brainstorming Phase Completed)
- **Target Framework**: .NET 10 (`net10.0`), C# 13

---

## 1. Overview & Motivation

ExamPrism previously organized HTTP endpoints under `apps/backend/src/Endpoints/` using a custom Minimal API discovery mechanism (`Common.Endpoints.IEndpoint` with reflection scanning in `EndpointExtensions`). 

While functional, this approach had several architectural limitations:
1. **Coupling to HTTP transport**: Logic was tightly bound to Minimal API `RouteGroupBuilder` / `IEndpointRouteBuilder`, preventing handlers from being reused directly in asynchronous background jobs or messaging pipelines without duplicate code or mediator wrappers.
2. **Boilerplate and reflection overhead**: Endpoint scanning relied on runtime reflection across loaded assemblies, and manual group configuration was required for filters and metadata.
3. **Naming and vertical boundaries**: Grouping by `Endpoints` encourages controller-like thinking rather than cohesive vertical feature slices.

This design transitions ExamPrism to a true **Vertical Slice Architecture**:
- Renaming `Endpoints` to `Features` using a new git commit (`git mv`), strictly preserving all previous git commit history intact.
- Adopting **Wolverine Full-Stack** (`WolverineFx`, `WolverineFx.Http`, `WolverineFx.Http.FluentValidation`, `WolverineFx.EntityFrameworkCore`).
- Adopting a **Message-First** pattern: Handlers are named cleanly (e.g. `LoginHandler`, `RegisterHandler` — omitting the `Endpoint` suffix) and can process messages either directly from HTTP (`Wolverine.Http`) or from asynchronous background queues / outbox processors.
- Deleting obsolete manual reflection scanning (`Common/Endpoints`).

---

## 2. Architectural Principles & Decisions

### 2.1 Git History Preservation
- The renaming from `apps/backend/src/Endpoints` to `apps/backend/src/Features` will be executed via `git mv` in a dedicated new commit.
- **Strict Rule**: No git history rewriting (no `git rebase -i`, no squashing, no amending of past commits). Prior commits and their hashes remain untouched.

### 2.2 Vertical Slice Directory Structure
All feature capabilities reside in isolated folders under `apps/backend/src/Features/`:

```
apps/backend/src/Features/
├── Auth/
│   ├── AuthCookieHelper.cs
│   ├── Login/
│   │   ├── LoginRequest.cs
│   │   ├── LoginResponse.cs
│   │   ├── LoginValidator.cs
│   │   └── LoginHandler.cs
│   ├── Register/
│   │   ├── RegisterRequest.cs
│   │   ├── RegisterResponse.cs
│   │   ├── RegisterValidator.cs
│   │   └── RegisterHandler.cs
│   ├── Refresh/
│   │   ├── RefreshHandler.cs
│   │   └── RefreshTokenResponse.cs
│   ├── Logout/
│   │   └── LogoutHandler.cs
│   └── GetMe/
│       ├── GetMeHandler.cs
│       └── UserProfileResponse.cs
├── Health/
│   └── HealthHandler.cs
└── Test/
    ├── TestExceptionHandlers.cs
    └── TestValidationHandlers.cs
```

### 2.3 Message-First Handler Naming & Design
- **No `*Endpoint` suffix**: Handlers are named by their domain action, e.g. `LoginHandler`, `RegisterHandler`, `SubmitExamHandler`.
- **Pure static methods where possible**: Wolverine executes static methods without object allocations.
- **Dual execution capability**:
  - **HTTP Endpoints**: Decorated with HTTP attributes (e.g., `[WolverinePost("/api/auth/login")]`, `[WolverineGet("/health")]`).
  - **Background Jobs**: Handlers processing messages without HTTP attributes act as message consumers / background jobs dispatched via `IMessageBus`.
  - **Seamless bridging**: An HTTP Handler can inject `IMessageBus bus` and publish messages (e.g. `bus.PublishAsync(new ProcessExamJob(id))`) with guaranteed delivery.

---

## 3. Package Dependencies & Central Management

Added to `apps/backend/Directory.Packages.props`:
```xml
<PackageVersion Include="WolverineFx" Version="4.8.0" />
<PackageVersion Include="WolverineFx.Http" Version="4.8.0" />
<PackageVersion Include="WolverineFx.Http.FluentValidation" Version="4.8.0" />
<PackageVersion Include="WolverineFx.EntityFrameworkCore" Version="4.8.0" />
```

Referenced in `apps/backend/src/ExamPrism.csproj`:
```xml
<PackageReference Include="WolverineFx" />
<PackageReference Include="WolverineFx.Http" />
<PackageReference Include="WolverineFx.Http.FluentValidation" />
<PackageReference Include="WolverineFx.EntityFrameworkCore" />
```

---

## 4. Wolverine Host & Pipeline Configuration

### 4.1 Program.cs Setup
In `apps/backend/src/Program.cs`:

```csharp
// Configure Wolverine Full-Stack Host
builder.Host.UseWolverine(opts =>
{
    // Auto-discover handlers in the ExamPrism assembly
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);

    // Transactional middleware & outbox support with EF Core AppDbContext
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
});
```

### 4.2 Application Pipeline
```csharp
app.UseAuthentication();
app.UseAuthorization();

// Replace app.MapEndpoints() with native Wolverine endpoint mapping
app.MapWolverineEndpoints(opts =>
{
    // Integrate FluentValidation problem details middleware
    opts.UseFluentValidationProblemDetailMiddleware();
});

app.Run();
```

---

## 5. Feature Implementation Details

### 5.1 Auth Feature Slices
- **Login (`Features/Auth/Login/LoginHandler.cs`)**:
  ```csharp
  namespace ExamPrism.Features.Auth.Login;

  public static class LoginHandler
  {
      [WolverinePost("/api/auth/login")]
      public static async Task<IResult> Handle(
          LoginRequest request,
          IAuthService authService,
          HttpContext httpContext)
      {
          var result = await authService.LoginAsync(request);
          AuthCookieHelper.SetTokenCookies(httpContext.Response, result.AccessToken, result.RefreshToken);
          return Results.Ok(result.User);
      }
  }
  ```
- **Register (`Features/Auth/Register/RegisterHandler.cs`)**:
  ```csharp
  namespace ExamPrism.Features.Auth.Register;

  public static class RegisterHandler
  {
      [WolverinePost("/api/auth/register")]
      public static async Task<IResult> Handle(
          RegisterRequest request,
          IAuthService authService,
          HttpContext httpContext)
      {
          var result = await authService.RegisterAsync(request);
          AuthCookieHelper.SetTokenCookies(httpContext.Response, result.AccessToken, result.RefreshToken);
          return Results.Created("/api/auth/me", result.User);
      }
  }
  ```
- **Refresh (`Features/Auth/Refresh/RefreshHandler.cs`)**:
  Extracts refresh token cookie, rotates tokens via `IAuthService`, updates cookies, and returns new access token or 401.
- **Logout (`Features/Auth/Logout/LogoutHandler.cs`)**:
  Clears authentication cookies and invalidates refresh token.
- **GetMe (`Features/Auth/GetMe/GetMeHandler.cs`)**:
  Decorated with `[Authorize]`, fetches current user profile from `IUserContext`.

### 5.2 Health Feature Slice
- **`Features/Health/HealthHandler.cs`**:
  ```csharp
  namespace ExamPrism.Features.Health;

  public static class HealthHandler
  {
      [WolverineGet("/health")]
      public static IResult Handle() => Results.Ok(new { status = "Healthy" });
  }
  ```
  *(Or mapped via ASP.NET Core `app.MapHealthChecks("/health")` with case-insensitive forwarding).*

### 5.3 Test Feature Slices (Development Only)
- `Features/Test/TestExceptionHandlers.cs`: Throws test exceptions (`NotFoundException`, `DomainException`, `UnauthorizedAccessException`) to verify `GlobalExceptionHandler`.
- `Features/Test/TestValidationHandlers.cs`: Endpoints to verify FluentValidation RFC 7807 response schema and localization.

---

## 6. Error Handling & Validation Continuity

1. **RFC 7807 ProblemDetails**:
   - `GlobalExceptionHandler` remains active in ASP.NET Core pipeline to catch domain exceptions and map them to standard RFC 7807 responses.
2. **FluentValidation**:
   - Request models continue using FluentValidation validators registered in DI.
   - Wolverine's validation middleware validates inputs and formats 400 ProblemDetails errors matching existing `ValidationProblemResponse`.
3. **Localization**:
   - `IStringLocalizer<SharedResources>` continues localizing error messages for Vietnamese (`vi-VN`) and English (`en-US`).

---

## 7. Deprecation & Cleanup

- **Delete**:
  - `apps/backend/src/Common/Endpoints/IEndpoint.cs`
  - `apps/backend/src/Common/Endpoints/EndpointExtensions.cs`
- **Namespace updates**:
  - Replace `ExamPrism.Endpoints.*` and `Common.Endpoints` with `ExamPrism.Features.*`.

---

## 8. Testing Strategy

1. **Unit Tests**:
   - Retain Identity, TokenService, UserContext, AuthCookieHelper, and GlobalExceptionHandler unit tests.
   - Remove/replace `EndpointExtensionsTests` with tests verifying Wolverine endpoint discovery.
   - Add direct unit tests for `*Handler` static methods.
2. **Integration Tests (`ExamPrism.Tests`)**:
   - Verify all API routes (`/api/auth/*`, `/health`, test endpoints) via `CustomWebApplicationFactory`.
   - Validate cookie issuance, token rotation, localization, and exception handling.
3. **Verification Command**:
   - `dotnet build apps/backend/ExamPrism.slnx`
   - `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName!~Integration"` (Unit test run)
   - Integration tests with Docker/Testcontainers when environment permits.
