# Design Document: Handler (`IHandler`) & Endpoint (`IEndpoint`) Separation and Multi-Channel Contracts

- **Author**: Antigravity Assistant & Engineering Team
- **Date**: 2026-11-01
- **Status**: Draft (Pending Written Spec Review)
- **Target Framework**: .NET 10 (`net10.0`), C# 13

---

## 1. Overview & Motivation

In the preceding iteration, ExamPrism combined HTTP endpoint routing (`[WolverineGet]` / `[WolverinePost]`) and domain execution logic directly inside `static class *Handler` methods in `apps/backend/src/Features/`. While concise, coupling HTTP concerns (`HttpRequest`, `HttpResponse`, HTTP cookies, `IResult` status codes) directly into `*Handler` classes prevented handlers from being invoked cleanly from background jobs, message queues, scheduled tasks, or other internal feature slices. Additionally, `static class` handlers could not implement a common marker interface for IDE discovery (`Find Implementations`) or uniform architectural governance.

This design establishes a clean two-layer contract while avoiding DTO duplication:
1. **Business Handlers (`Features/` — `IHandler`)**: Pure, transport-agnostic use-case handlers powered by **Wolverine (`IMessageBus`)**. Every handler implements `Common.Handlers.IHandler` (`: IWolverineHandler`), uses Wolverine's zero-allocation `public static` method execution and Method Injection, has zero dependency on `Microsoft.AspNetCore.Http`, and can be invoked identically from HTTP APIs, Background Jobs, or Message Queues.
2. **HTTP Endpoints (`Endpoints/` — `IEndpoint`)**: Thin ASP.NET Core 10 Minimal API transport adapters implementing `Common.Endpoints.IEndpoint` (`static abstract void MapEndpoint(IEndpointRouteBuilder app)`), automatically discovered and registered at startup via `app.MapEndpoints()`. Endpoints handle routing, OpenAPI metadata, HTTP cookies, and HTTP status codes while reusing the Handler's input/output records directly without creating intermediate DTOs.
3. **Architecture Enforcement (`ArchitectureTests`)**: Automated reflection-based architecture tests enforcing handler signatures, naming conventions (`*Command`, `*Query`, `*Event`, `*Result`), and strict layer isolation between `Endpoints/` and `Features/`.

---

## 2. Core Contracts & Auto-Registration

### 2.1 Business Handler Contract (`Common.Handlers.IHandler`)
- **Location**: `apps/backend/src/Common/Handlers/IHandler.cs`
- **Definition**:
  ```csharp
  namespace Common.Handlers;

  using Wolverine;

  /// <summary>
  /// Marker contract for all application use-case and message handlers.
  /// Enables IDE implementation discovery, Wolverine handler discovery,
  /// and automated architectural verification while preserving static method injection.
  /// </summary>
  public interface IHandler : IWolverineHandler;
  ```
- **Implementation Convention**:
  - Concrete handlers are declared as `public sealed class <Action>Handler : IHandler`.
  - Each handler exposes **exactly one** `public static` entry method named `Handle` or `HandleAsync` (optional Wolverine compound lifecycle methods `Load`, `LoadAsync`, `Before`, `BeforeAsync`, `After`, `AfterAsync`, `Validate`, `ValidateAsync` are permitted; internal helpers must be `private`).
  - Keeping `Handle` / `HandleAsync` `static` ensures Wolverine generates zero-allocation static invocation code with full Method Injection support.

### 2.2 HTTP Endpoint Contract (`Common.Endpoints.IEndpoint`)
- **Location**: `apps/backend/src/Common/Endpoints/IEndpoint.cs`
- **Definition**:
  ```csharp
  namespace Common.Endpoints;

  using Microsoft.AspNetCore.Routing;

  /// <summary>
  /// Compile-time contract for ASP.NET Core Minimal API endpoints.
  /// Uses C# static abstract members for zero-allocation startup route registration.
  /// </summary>
  public interface IEndpoint
  {
      static abstract void MapEndpoint(IEndpointRouteBuilder app);
  }
  ```

### 2.3 Automated Endpoint Discovery (`Common.Endpoints.EndpointExtensions`)
- **Location**: `apps/backend/src/Common/Endpoints/EndpointExtensions.cs`
- **Behavior**:
  - `app.MapEndpoints(params Assembly[] assemblies)` (and `app.MapEndpoints<TMarker>()`) scans target assemblies (defaulting to the ExamPrism application assembly) for non-abstract, non-interface, closed concrete types implementing `IEndpoint`.
  - **Environment Filtering**: Checks `app.ServiceProvider.GetService<IHostEnvironment>()`. When the environment is **not** `Development` (`!env.IsDevelopment()`), types in namespace `ExamPrism.Endpoints.Test` (and sub-namespaces) are automatically skipped so test fixture routes are never exposed in non-Development environments.
  - **Automatic FluentValidation Filter**: Creates a root `RouteGroupBuilder` (`app.MapGroup("").AddEndpointFilterFactory(ValidationFilterFactory.Create)`) and passes it to each `IEndpoint.MapEndpoint` invocation, ensuring all endpoints with registered `IValidator<T>` parameters automatically return localized `ValidationProblemResponse` (RFC 7807) payloads and honor `[SkipValidation]`.
  - Supports both standard `public static void MapEndpoint(IEndpointRouteBuilder)` and explicit static interface implementations, unwrapping `TargetInvocationException` via `ExceptionDispatchInfo`.

---

## 3. Directory Structure & Transport-Agnostic Naming

### 3.1 Directory Layout

```text
apps/backend/src/
├── Common/
│   ├── Endpoints/
│   │   ├── IEndpoint.cs
│   │   └── EndpointExtensions.cs
│   ├── Exceptions/
│   ├── Handlers/
│   │   └── IHandler.cs
│   ├── Localization/
│   ├── Security/
│   └── Validation/
│       ├── SkipValidationAttribute.cs
│       ├── ValidationError.cs
│       ├── ValidationFilterFactory.cs
│       └── ValidationProblemResponse.cs
├── Endpoints/                              # HTTP Transport Layer ONLY
│   ├── Auth/
│   │   ├── AuthCookieHelper.cs
│   │   ├── RegisterEndpoint.cs
│   │   ├── LoginEndpoint.cs
│   │   ├── RefreshEndpoint.cs
│   │   ├── LogoutEndpoint.cs
│   │   └── GetMeEndpoint.cs
│   ├── Health/
│   │   └── GetHealthEndpoint.cs
│   └── Test/                               # Development-only HTTP test fixtures
│       ├── TestExceptionEndpoints.cs
│       └── TestValidationEndpoints.cs
└── Features/                               # Transport-Agnostic Application Layer ONLY
    ├── Auth/
    │   ├── Register/
    │   │   ├── RegisterCommand.cs          # Contains RegisterCommand & RegisterResult
    │   │   └── RegisterHandler.cs
    │   ├── Login/
    │   │   ├── LoginCommand.cs             # Contains LoginCommand & LoginResult
    │   │   └── LoginHandler.cs
    │   ├── Refresh/
    │   │   ├── RefreshTokenCommand.cs      # Contains RefreshTokenCommand & RefreshTokenResult
    │   │   └── RefreshTokenHandler.cs
    │   ├── Logout/
    │   │   ├── LogoutCommand.cs
    │   │   └── LogoutHandler.cs
    │   └── GetMe/
    │       ├── GetMeQuery.cs               # Contains GetMeQuery & GetMeResult
    │       └── GetMeHandler.cs
    └── Health/
        └── GetHealth/
            ├── GetHealthQuery.cs           # Contains GetHealthQuery & GetHealthResult
            └── GetHealthHandler.cs
```

### 3.2 Transport-Agnostic Input & Output Naming (`CQRS + Result`)
To keep handlers natural across **HTTP APIs, Background Jobs, and Message Queues**, HTTP-centric `*Request` / `*Response` names are replaced by:
- **Inputs (always the 1st parameter of `Handle` / `HandleAsync`)**:
  - `*Command`: State-changing actions or background tasks (e.g., `RegisterCommand`, `LoginCommand`, `RefreshTokenCommand`, `LogoutCommand`).
  - `*Query`: Read-only data retrieval operations (e.g., `GetMeQuery`, `GetHealthQuery`).
  - `*Event`: Asynchronous 1-to-many domain/integration notifications (e.g., `UserRegisteredEvent`).
- **Outputs**:
  - `*Result`: Returned data from a command or query (e.g., `RegisterResult`, `GetMeResult`, `GetHealthResult`), or `Task` / `ValueTask` / `void` when no output is produced (`LogoutCommand`, `*Event`).
- **Direct DTO Sharing (No Extra Endpoint DTOs)**:
  - Endpoints accept the Handler's `*Command` / `*Query` directly (from JSON body, `[AsParameters]` query/route binding, or constructed from cookies/headers when no body exists) and serialize the Handler's `*Result` directly as the HTTP response payload.
  - When a Handler output carries transport metadata needed by an Endpoint (such as raw JWT/Refresh tokens for setting `HttpOnly` cookies, or boolean health flags for choosing HTTP status codes) that must not appear in JSON responses, those properties are decorated with `[property: JsonIgnore]` on the `*Result` record.

---

## 4. Multi-Channel Execution, Validation & Transaction Flow

### 4.1 Multi-Channel Invocation via `IMessageBus`
Because every `*Handler : IHandler` takes its message (`*Command` / `*Query` / `*Event`) as its first parameter and has zero HTTP dependencies, callers invoke the exact same handler across all channels:
- **HTTP Endpoint (Synchronous Request/Reply)**:
  ```csharp
  var result = await bus.InvokeAsync<RegisterResult>(command, ct);
  ```
- **Background Job / Delayed Execution**:
  ```csharp
  await bus.ScheduleAsync(new LogoutCommand(rawToken), TimeSpan.FromMinutes(15));
  ```
- **Message Queue / Transactional Outbox**:
  ```csharp
  await bus.PublishAsync(new RegisterCommand(email, password));
  ```

### 4.2 Two-Tier Validation with a Single `AbstractValidator<T>`
- **HTTP Tier**: `ValidationFilterFactory` validates endpoint arguments before entering the endpoint delegate, returning localized `400 Bad Request` (`ValidationProblemResponse`) payloads.
- **Message Bus Tier**: `Program.cs` configures `opts.UseFluentValidation()` (via `WolverineFx.FluentValidation`) inside `builder.Host.UseWolverine(...)` so commands/queries dispatched directly from Background Jobs or Message Queues are also validated before executing `Handle`.

### 4.3 Error Handling & Transaction Commit Semantics
- **Domain & Validation Failures**:
  - Handlers throw transport-neutral exceptions (`FluentValidation.ValidationException`, `DomainException`, `NotFoundException`, `UnauthorizedAccessException`).
  - On HTTP requests, `GlobalExceptionHandler` translates these exceptions into standardized RFC 7807 `ProblemDetails` / `ValidationProblemResponse` status codes (`400`, `401`, `404`, `409`, `500`).
  - On Queue / Background Job executions, exceptions propagate to Wolverine's error-handling and retry / dead-letter policies.
- **Compromised Refresh Token Reuse & Auto-Transactions**:
  - Wolverine runs with `opts.UseEntityFrameworkCoreTransactions()` and `opts.Policies.AutoApplyTransactions()`.
  - In `RefreshTokenHandler`, detecting a replayed revoked refresh token revokes all active tokens for that user in the database (`ExecuteUpdateAsync`) and rejects the refresh attempt. Throwing an exception after revoking tokens inside an active transaction would roll back the revocation.
  - Therefore, `RefreshTokenHandler.Handle` returns `Task<RefreshTokenResult?>`, returning `null` when the token is missing, unknown, expired, or revoked (after revoking all active user tokens on reuse). Wolverine commits the revocation cleanly, and `RefreshEndpoint` clears authentication cookies and returns `Results.Unauthorized()` whenever the result is `null`.

---

## 5. Slice Migration Specifications

### 5.1 `Auth/Register`
- **Messages (`ExamPrism.Features.Auth.Register`)**:
  - `public record RegisterCommand(string Email, string Password);`
  - `public record RegisterResult(Guid Id, string Email, [property: JsonIgnore] string AccessToken, [property: JsonIgnore] string RefreshToken);`
- **Handler (`RegisterHandler : IHandler`)**:
  - Validates non-empty email/password and duplicate email, throwing `FluentValidation.ValidationException` (mapped to `400` by `GlobalExceptionHandler`).
  - Creates `ApplicationUser` via `UserManager<ApplicationUser>` (mapping identity errors to `ValidationException`), generates access + refresh tokens, persists `RefreshToken` to `AppDbContext`, and returns `RegisterResult`.
- **Endpoint (`ExamPrism.Endpoints.Auth.RegisterEndpoint : IEndpoint`)**:
  - Maps `POST /api/auth/register` (`WithTags("Authentication")`).
  - Invokes `bus.InvokeAsync<RegisterResult>(command, ct)`, calls `AuthCookieHelper.SetAuthCookies(...)`, and returns `Results.Created("/api/auth/me", result)`.

### 5.2 `Auth/Login`
- **Messages (`ExamPrism.Features.Auth.Login`)**:
  - `public record LoginCommand(string Email, string Password);`
  - `public record LoginResult(Guid Id, string Email, [property: JsonIgnore] string AccessToken, [property: JsonIgnore] string RefreshToken);`
- **Handler (`LoginHandler : IHandler`)**:
  - Checks non-empty credentials, user lookup, and password verification. Throws `UnauthorizedAccessException("Invalid credentials.")` on failure (mapped to `401` by `GlobalExceptionHandler`).
  - Generates access + refresh tokens, persists `RefreshToken`, and returns `LoginResult`.
- **Endpoint (`ExamPrism.Endpoints.Auth.LoginEndpoint : IEndpoint`)**:
  - Maps `POST /api/auth/login` (`WithTags("Authentication")`).
  - Invokes `bus.InvokeAsync<LoginResult>(command, ct)`, sets auth cookies, and returns `Results.Ok(result)`.

### 5.3 `Auth/Refresh`
- **Messages (`ExamPrism.Features.Auth.Refresh`)**:
  - `public record RefreshTokenCommand(string RefreshToken);`
  - `public record RefreshTokenResult([property: JsonIgnore] string AccessToken, [property: JsonIgnore] string RefreshToken);`
- **Handler (`RefreshTokenHandler : IHandler`)**:
  - Returns `null` if `RefreshToken` is blank, not found, or expired.
  - If `token.IsRevoked || token.ReplacedByTokenHash != null`, revokes all non-revoked tokens for `token.UserId` and returns `null`.
  - Otherwise rotates token, persists new `RefreshToken`, and returns `RefreshTokenResult`.
- **Endpoint (`ExamPrism.Endpoints.Auth.RefreshEndpoint : IEndpoint`)**:
  - Maps `POST /api/auth/refresh` (`WithTags("Authentication")`).
  - Reads `AuthCookieHelper.RefreshTokenCookieName` from `HttpRequest.Cookies`; if missing/blank, returns `Results.Unauthorized()`.
  - Invokes `bus.InvokeAsync<RefreshTokenResult?>(new RefreshTokenCommand(rawRefresh), ct)`. If `null`, calls `AuthCookieHelper.ClearAuthCookies(response)` and returns `Results.Unauthorized()`; otherwise sets new cookies and returns `Results.Ok()`.

### 5.4 `Auth/Logout`
- **Messages (`ExamPrism.Features.Auth.Logout`)**:
  - `public record LogoutCommand(string? RefreshToken);`
- **Handler (`LogoutHandler : IHandler`)**:
  - `public static async Task Handle(LogoutCommand command, AppDbContext db, ITokenService tokenService, TimeProvider timeProvider, CancellationToken ct)`
  - If `command.RefreshToken` is non-empty and matches an active `RefreshToken`, marks `RevokedAt = timeProvider.GetUtcNow()` and saves changes.
- **Endpoint (`ExamPrism.Endpoints.Auth.LogoutEndpoint : IEndpoint`)**:
  - Maps `POST /api/auth/logout` (`WithTags("Authentication")`).
  - Extracts optional refresh token cookie, invokes `bus.InvokeAsync(new LogoutCommand(rawRefresh), ct)`, clears auth cookies, and returns `Results.NoContent()`.

### 5.5 `Auth/GetMe`
- **Messages (`ExamPrism.Features.Auth.GetMe`)**:
  - `public record GetMeQuery;`
  - `public record GetMeResult(Guid Id, string Email);`
- **Handler (`GetMeHandler : IHandler`)**:
  - Reads `userContext.UserId` (throwing `UnauthorizedAccessException` if null) and loads user via `UserManager<ApplicationUser>` (throwing `NotFoundException` if null). Returns `GetMeResult`.
- **Endpoint (`ExamPrism.Endpoints.Auth.GetMeEndpoint : IEndpoint`)**:
  - Maps `GET /api/auth/me` with `.RequireAuthorization()` and `.WithTags("Authentication")`.
  - Invokes `bus.InvokeAsync<GetMeResult>(new GetMeQuery(), ct)` and returns `Results.Ok(result)`.

### 5.6 `Health/GetHealth`
- **Messages (`ExamPrism.Features.Health.GetHealth`)**:
  - `public record GetHealthQuery;`
  - `public record GetHealthResult(string Status, DateTime Timestamp, [property: JsonIgnore] bool IsHealthy);`
- **Handler (`GetHealthHandler : IHandler`)**:
  - Executes `healthCheckService.CheckHealthAsync(ct)` and returns `GetHealthResult(report.Status.ToString(), DateTime.UtcNow, report.Status == HealthStatus.Healthy)`.
- **Endpoint (`ExamPrism.Endpoints.Health.GetHealthEndpoint : IEndpoint`)**:
  - Maps `GET /health` (`WithTags("Health")`).
  - Invokes `bus.InvokeAsync<GetHealthResult>(new GetHealthQuery(), ct)` and returns `result.IsHealthy ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)`.

### 5.7 `Endpoints/Test` (Development Fixtures)
- Move `Features/Test/TestExceptionHandlers.cs` to `Endpoints/Test/TestExceptionEndpoints.cs` (`namespace ExamPrism.Endpoints.Test`, `public sealed class TestExceptionEndpoints : IEndpoint`) mapping `/api/test/exception/*` routes.
- Move `Features/Test/TestValidationHandlers.cs` to `Endpoints/Test/TestValidationEndpoints.cs` (`namespace ExamPrism.Endpoints.Test`, `public sealed class TestValidationEndpoints : IEndpoint`) mapping `/api/test/validation*` routes (preserving `[SkipValidation]` on `/api/test/validation/skip`).
- `EndpointExtensions.MapEndpoints()` skips `ExamPrism.Endpoints.Test` whenever `!environment.IsDevelopment()`.

---

## 6. Dependencies & `Program.cs` Updates

### 6.1 Package Changes
- **`apps/backend/Directory.Packages.props` & `apps/backend/src/ExamPrism.csproj`**:
  - **Remove**: `WolverineFx.Http`, `WolverineFx.Http.FluentValidation`
  - **Keep**: `WolverineFx` (`4.8.0`), `WolverineFx.EntityFrameworkCore` (`4.8.0`)
  - **Add**: `WolverineFx.FluentValidation` (`4.8.0`)
- **Delete Obsolete File**:
  - `apps/backend/src/Common/Validation/CustomProblemDetailSource.cs`

### 6.2 `Program.cs` Pipeline
```csharp
builder.Host.UseWolverine(opts =>
{
    opts.Discovery.IncludeAssembly(typeof(Program).Assembly);
    opts.UseFluentValidation();
    opts.UseEntityFrameworkCoreTransactions();
    opts.Policies.AutoApplyTransactions();
});
```
- Remove `builder.Services.AddTransient(typeof(IProblemDetailSource<>), typeof(CustomProblemDetailSource<>));` and `builder.Services.AddWolverineHttp();`.
- Replace `app.MapWolverineEndpoints(...)` with `app.MapEndpoints();` (`using Common.Endpoints;`).

---

## 7. Architecture Tests & Verification Strategy

### 7.1 `ArchitectureTests.cs` (`apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`)
Implemented using `System.Reflection` and `FluentAssertions` with zero additional NuGet packages:
1. **`IHandler` Identity & Placement**:
   - Every type implementing `IHandler` must reside in `ExamPrism.Features..*`, be `public` and `sealed`, and have a name ending with `Handler`.
   - Every class in `ExamPrism.Features..*` whose name ends with `Handler` must implement `IHandler`.
2. **`IHandler` Method Signature Contract**:
   - Every `IHandler` implementation must declare **exactly one** `public static` entry method named `Handle` or `HandleAsync` (other `public` declared methods may only be Wolverine lifecycle methods: `Load`, `LoadAsync`, `Before`, `BeforeAsync`, `After`, `AfterAsync`, `Validate`, `ValidateAsync`).
   - The first parameter of `Handle` / `HandleAsync` must exist and its type name must end with `Command`, `Query`, or `Event`.
   - The return type of `Handle` / `HandleAsync` must be `void`, `Task`, `ValueTask`, a type ending with `Result`, or `Task<T>` / `ValueTask<T>` where `T` ends with `Result`.
3. **Strict HTTP Isolation in `Features/`**:
   - No type in `ExamPrism.Features..*` (handlers, commands, queries, events, results, or helpers) may declare fields, properties, constructors, or method signatures referencing any type in `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing`, or `Microsoft.AspNetCore.Mvc`.
4. **`IEndpoint` Identity, Placement & Persistence Isolation**:
   - Every type implementing `IEndpoint` must reside in `ExamPrism.Endpoints..*`, be `public` and `sealed`, and end with `Endpoint` (or `Endpoints` inside `ExamPrism.Endpoints.Test`).
   - Every class in `ExamPrism.Endpoints..*` ending with `Endpoint` or `Endpoints` must implement `IEndpoint`.
   - No method declared on any type in `ExamPrism.Endpoints..*` may accept `AppDbContext` as a parameter.

### 7.2 Unit & Integration Testing
1. **Endpoint Auto-Discovery Unit Tests (`EndpointExtensionsTests.cs`)**:
   - Verify automatic discovery and mapping of `IEndpoint` classes, explicit interface implementation support, abstract class filtering, and exclusion of `ExamPrism.Endpoints.Test` in non-Development environments.
2. **Handler Unit Tests (`WolverineHandlerTests.cs`)**:
   - Test `RegisterHandler`, `LoginHandler`, `RefreshTokenHandler`, `LogoutHandler`, `GetMeHandler`, and `GetHealthHandler` directly as pure C# static methods without `DefaultHttpContext` mocks.
3. **End-to-End & Helper Tests**:
   - Update namespace/type imports in `AuthCookieHelperTests.cs`, `AuthEndpointTests.cs`, `HealthEndpointTests.cs`, and `PipelineIntegrationTests.cs` while keeping all HTTP assertions, cookie checks, localization checks, and RFC 7807 ProblemDetails assertions intact.
