# Handler & Endpoint Contract Separation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decouple HTTP routing (`Endpoints/` via `Common.Endpoints.IEndpoint`) from transport-agnostic business handlers (`Features/` via `Common.Handlers.IHandler`) sharing `*Command`/`*Query`/`*Event` and `*Result` DTOs directly across HTTP APIs, Background Jobs, and Message Queues, enforced by reflection architecture tests.

**Architecture:** `Endpoints/` contains thin ASP.NET Core 10 Minimal API classes implementing `IEndpoint` (`static abstract void MapEndpoint(IEndpointRouteBuilder app)`), auto-registered via `app.MapEndpoints()` with `ValidationFilterFactory` and non-Development `ExamPrism.Endpoints.Test` filtering, delegating all work to Wolverine's `IMessageBus`. `Features/` contains `public sealed class *Handler : IHandler` (`: IWolverineHandler`) classes exposing a single `public static` `Handle`/`HandleAsync` method with zero HTTP dependencies, backed by `WolverineFx.FluentValidation` and EF Core auto-transactions.

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core 10 Minimal APIs, WolverineFx 4.8.0, WolverineFx.FluentValidation 4.8.0, WolverineFx.EntityFrameworkCore 4.8.0, FluentValidation 12.2.0, EF Core / Npgsql, xUnit, FluentAssertions, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-01-handler-endpoint-contracts-design.md`

## Global Constraints

- Target framework `.NET 10` (`net10.0`), C# 13, Central Package Management in `apps/backend/Directory.Packages.props`.
- Preserve git commit history strictly: no `git rebase`, no `--amend`, no squashing of prior commits.
- Every business handler in `ExamPrism.Features..*` must be `public sealed class <Action>Handler : IHandler` with one `public static` `Handle` or `HandleAsync` entry method whose first parameter ends with `Command`, `Query`, or `Event` and whose return type is `void`, `Task`, `ValueTask`, `*Result`, `Task<*Result>`, or `ValueTask<*Result>`.
- `ExamPrism.Features..*` must have zero references to `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing`, or `Microsoft.AspNetCore.Mvc`.
- Every HTTP endpoint in `ExamPrism.Endpoints..*` must be `public sealed class *Endpoint : IEndpoint` (or `*Endpoints` in `ExamPrism.Endpoints.Test`), must never inject `AppDbContext` directly, and must share the handler's input/output records directly without extra intermediate DTOs.
- Preserve existing RFC 7807 `ProblemDetails` / `ValidationProblemResponse` payloads, `vi-VN`/`en-US` localization, and `HttpOnly` auth cookie behavior.

## Review Focus

1. **Token leakage in JSON payloads (`RegisterResult` / `LoginResult`)**: Serializing `RegisterResult` or `LoginResult` directly as the HTTP response body must omit `AccessToken` and `RefreshToken` via `[property: JsonIgnore]`, while `ReadFromJsonAsync<RegisterResult>()` still deserializes `{ id, email }` cleanly using default parameter values (`= ""`).
2. **Compromised refresh token revocation rolled back on error**: `RefreshTokenHandler.Handle` must return `Task<RefreshTokenResult?>` (`null` on invalid, expired, or replayed revoked tokens) rather than throwing after `ExecuteUpdateAsync`, so Wolverine's `AutoApplyTransactions()` commits the revocation of all active user tokens while `RefreshEndpoint` clears cookies and returns `401`.
3. **Development-only test endpoints exposed outside Development**: `EndpointExtensions.MapEndpoints()` must inspect `IHostEnvironment` from `app.ServiceProvider` and skip `ExamPrism.Endpoints.Test` whenever `!env.IsDevelopment()`.
4. **Direct `IMessageBus` calls bypassing HTTP validation**: Dispatching an invalid message with a registered `AbstractValidator<T>` directly via `IMessageBus.InvokeAsync` (simulating a Background Job or Message Queue) must throw `FluentValidation.ValidationException` via `opts.UseFluentValidation()`.
5. **Generic / nullable `Task<TResult?>` return types in reflection checks**: `ArchitectureTests` must unwrap `Task<T>` and `ValueTask<T>` so `Task<RefreshTokenResult?>` passes the `*Result` return-type rule while `Task<IResult>` or `*Response` fails.

---

### Task 1: Core Contracts (`IHandler`, `IEndpoint`), Automated Discovery (`EndpointExtensions`) & `WolverineFx.FluentValidation` Package

**Files:**
- Modify: `apps/backend/Directory.Packages.props:17-21`
- Modify: `apps/backend/src/ExamPrism.csproj:15-19`
- Create: `apps/backend/src/Common/Handlers/IHandler.cs`
- Create: `apps/backend/src/Common/Endpoints/IEndpoint.cs`
- Create: `apps/backend/src/Common/Endpoints/EndpointExtensions.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/EndpointExtensionsTests.cs`

**Interfaces:**
- Consumes: `Wolverine.IWolverineHandler`, `Common.Validation.ValidationFilterFactory`
- Produces:
  - `Common.Handlers.IHandler : IWolverineHandler`
  - `Common.Endpoints.IEndpoint` (`static abstract void MapEndpoint(IEndpointRouteBuilder app);`)
  - `Common.Endpoints.EndpointExtensions.MapEndpoints(this IEndpointRouteBuilder app, params Assembly[] assemblies)`
  - `Common.Endpoints.EndpointExtensions.MapEndpoints<TMarker>(this IEndpointRouteBuilder app)`

- [ ] **Step 1: Add `WolverineFx.FluentValidation` (`4.8.0`) to Central Package Management and `ExamPrism.csproj`**

In `apps/backend/Directory.Packages.props`, add `<PackageVersion Include="WolverineFx.FluentValidation" Version="4.8.0" />`. In `apps/backend/src/ExamPrism.csproj`, add `<PackageReference Include="WolverineFx.FluentValidation" />` (leave `WolverineFx.Http*` in place until Task 3 removes the legacy `[WolverineGet/Post]` files).

- [ ] **Step 2: Write failing unit tests for `IEndpoint` discovery and environment filtering in `apps/backend/tests/ExamPrism.Tests/Infrastructure/EndpointExtensionsTests.cs`**

Cover four behaviors using `WebApplication.CreateBuilder` with explicit test assemblies/types (including a fixture endpoint inside `namespace ExamPrism.Endpoints.Test`):
```csharp
[Fact]
public void MapEndpoints_DiscoversPublicAndExplicitIEndpointImplementations_AndIgnoresAbstractTypes()
{
    var builder = WebApplication.CreateBuilder();
    builder.Environment.EnvironmentName = Environments.Development;
    using var app = builder.Build();

    app.MapEndpoints(typeof(EndpointExtensionsTests).Assembly);

    var routePatterns = ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(ds => ds.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(e => e.RoutePattern.RawText)
        .ToList();

    routePatterns.Should().Contain("/test-discovery/public");
    routePatterns.Should().Contain("/test-discovery/explicit");
    routePatterns.Should().NotContain("/test-discovery/abstract");
}

[Fact]
public void MapEndpoints_WhenEnvironmentIsNotDevelopment_SkipsEndpointsTestNamespace()
{
    var builder = WebApplication.CreateBuilder();
    builder.Environment.EnvironmentName = Environments.Production;
    using var app = builder.Build();

    app.MapEndpoints(typeof(EndpointExtensionsTests).Assembly);

    var routePatterns = ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(ds => ds.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(e => e.RoutePattern.RawText)
        .ToList();

    routePatterns.Should().Contain("/test-discovery/public");
    routePatterns.Should().NotContain("/test-discovery/dev-only-fixture");
}
```

- [ ] **Step 3: Run `EndpointExtensionsTests` to verify compilation/test failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~EndpointExtensionsTests"`
Expected: FAIL (`Common.Endpoints` / `IEndpoint` / `MapEndpoints` do not exist yet).

- [ ] **Step 4: Implement `IHandler`, `IEndpoint`, and `EndpointExtensions`**

- `apps/backend/src/Common/Handlers/IHandler.cs`: `namespace Common.Handlers; using Wolverine; public interface IHandler : IWolverineHandler;`
- `apps/backend/src/Common/Endpoints/IEndpoint.cs`: `namespace Common.Endpoints; using Microsoft.AspNetCore.Routing; public interface IEndpoint { static abstract void MapEndpoint(IEndpointRouteBuilder app); }`
- `apps/backend/src/Common/Endpoints/EndpointExtensions.cs`:
  - `public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder app, params Assembly[] assemblies)` and `MapEndpoints<TMarker>(this IEndpointRouteBuilder app)`
  - Default assembly resolution when `assemblies.Length == 0`: `[typeof(EndpointExtensions).Assembly]`.
  - Filter types: `!t.IsAbstract && !t.IsInterface && !t.IsGenericTypeDefinition && !t.ContainsGenericParameters && typeof(IEndpoint).IsAssignableFrom(t)`.
  - Check `var env = app.ServiceProvider.GetService<IHostEnvironment>();` — if `env is not null && !env.IsDevelopment()`, skip any type `t` where `t.Namespace == "ExamPrism.Endpoints.Test" || (t.Namespace?.StartsWith("ExamPrism.Endpoints.Test.") ?? false)`.
  - Create root group `var validationGroup = app.MapGroup("").AddEndpointFilterFactory(ValidationFilterFactory.Create);` and invoke each type's `MapEndpoint` (resolving public/non-public static `MapEndpoint` or `type.GetInterfaceMap(typeof(IEndpoint))` fallback, unwrapping `TargetInvocationException` via `ExceptionDispatchInfo`).

- [ ] **Step 5: Run `EndpointExtensionsTests` to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~EndpointExtensionsTests"`
Expected: PASS (all discovery and non-Development exclusion tests succeed).

- [ ] **Step 6: Commit**

```bash
git add apps/backend/Directory.Packages.props apps/backend/src/ExamPrism.csproj apps/backend/src/Common/Handlers/IHandler.cs apps/backend/src/Common/Endpoints/IEndpoint.cs apps/backend/src/Common/Endpoints/EndpointExtensions.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/EndpointExtensionsTests.cs
git commit -m "feat(common): add IHandler, IEndpoint, and environment-aware MapEndpoints discovery"
```

---

### Task 2: Transport-Agnostic `Features/` Slices (`Auth` & `Health`) and Pure Handler Unit Tests

**Files:**
- Create: `apps/backend/src/Features/Health/GetHealth/GetHealthQuery.cs`
- Create: `apps/backend/src/Features/Health/GetHealth/GetHealthHandler.cs`
- Create: `apps/backend/src/Features/Auth/Register/RegisterCommand.cs`
- Create: `apps/backend/src/Features/Auth/Register/RegisterHandler.cs`
- Create: `apps/backend/src/Features/Auth/Login/LoginCommand.cs`
- Create: `apps/backend/src/Features/Auth/Login/LoginHandler.cs`
- Create: `apps/backend/src/Features/Auth/Refresh/RefreshTokenCommand.cs`
- Create: `apps/backend/src/Features/Auth/Refresh/RefreshTokenHandler.cs`
- Create: `apps/backend/src/Features/Auth/Logout/LogoutCommand.cs`
- Create: `apps/backend/src/Features/Auth/Logout/LogoutHandler.cs`
- Create: `apps/backend/src/Features/Auth/GetMe/GetMeQuery.cs`
- Create: `apps/backend/src/Features/Auth/GetMe/GetMeHandler.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Features/WolverineHandlerTests.cs`

**Interfaces:**
- Consumes: `Common.Handlers.IHandler`, `Common.Exceptions.NotFoundException`, `Common.Security.IUserContext`, `ExamPrism.Infrastructure.Identity.*`, `ExamPrism.Infrastructure.Persistence.AppDbContext`
- Produces:
  - `ExamPrism.Features.Health.GetHealth`:
    - `public record GetHealthQuery;`
    - `public record GetHealthResult(string Status, DateTime Timestamp, [property: JsonIgnore] bool IsHealthy = true);`
    - `GetHealthHandler.Handle(GetHealthQuery query, HealthCheckService healthCheckService, CancellationToken ct = default) -> Task<GetHealthResult>`
  - `ExamPrism.Features.Auth.Register`:
    - `public record RegisterCommand(string Email, string Password);`
    - `public record RegisterResult(Guid Id, string Email, [property: JsonIgnore] string AccessToken = "", [property: JsonIgnore] string RefreshToken = "");`
    - `RegisterHandler.Handle(RegisterCommand command, UserManager<ApplicationUser> userManager, AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions, TimeProvider timeProvider, CancellationToken ct = default) -> Task<RegisterResult>`
  - `ExamPrism.Features.Auth.Login`:
    - `public record LoginCommand(string Email, string Password);`
    - `public record LoginResult(Guid Id, string Email, [property: JsonIgnore] string AccessToken = "", [property: JsonIgnore] string RefreshToken = "");`
    - `LoginHandler.Handle(LoginCommand command, UserManager<ApplicationUser> userManager, AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions, TimeProvider timeProvider, CancellationToken ct = default) -> Task<LoginResult>`
  - `ExamPrism.Features.Auth.Refresh`:
    - `public record RefreshTokenCommand(string RefreshToken);`
    - `public record RefreshTokenResult([property: JsonIgnore] string AccessToken = "", [property: JsonIgnore] string RefreshToken = "");`
    - `RefreshTokenHandler.Handle(RefreshTokenCommand command, AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions, TimeProvider timeProvider, CancellationToken ct = default) -> Task<RefreshTokenResult?>`
  - `ExamPrism.Features.Auth.Logout`:
    - `public record LogoutCommand(string? RefreshToken);`
    - `LogoutHandler.Handle(LogoutCommand command, AppDbContext db, ITokenService tokenService, TimeProvider timeProvider, CancellationToken ct = default) -> Task`
  - `ExamPrism.Features.Auth.GetMe`:
    - `public record GetMeQuery;`
    - `public record GetMeResult(Guid Id, string Email);`
    - `GetMeHandler.Handle(GetMeQuery query, IUserContext userContext, UserManager<ApplicationUser> userManager, CancellationToken ct = default) -> Task<GetMeResult>`

- [ ] **Step 1: Rewrite `apps/backend/tests/ExamPrism.Tests/Features/WolverineHandlerTests.cs` against the new pure transport-agnostic handlers and `[JsonIgnore]` contracts**

Replace `DefaultHttpContext` mocks with pure message/result assertions covering:
- `LoginHandler_WhenCredentialsAreEmpty_ThrowsUnauthorizedAccessException` (`await FluentActions.Invoking(() => LoginHandler.Handle(new LoginCommand("", ""), ...)).Should().ThrowAsync<UnauthorizedAccessException>()`)
- `RegisterHandler_WhenCredentialsAreEmpty_ThrowsValidationException` (`ThrowAsync<FluentValidation.ValidationException>()`)
- `RegisterResult_And_LoginResult_JsonSerialization_OmitsTokensAndRoundTripsPublicFields`:
  ```csharp
  var original = new RegisterResult(Guid.NewGuid(), "user@example.com", "secret-access", "secret-refresh");
  var json = JsonSerializer.Serialize(original);
  json.Should().NotContain("secret-access").And.NotContain("secret-refresh");
  var deserialized = JsonSerializer.Deserialize<RegisterResult>(json);
  deserialized.Should().NotBeNull();
  deserialized!.Id.Should().Be(original.Id);
  deserialized.Email.Should().Be("user@example.com");
  ```
- `GetHealthHandler_WhenHealthy_ReturnsIsHealthyTrue_AndOmitsIsHealthyFromJson` (and `WhenUnhealthy_ReturnsIsHealthyFalse`)
- `GetMeHandler_WhenUserContextHasNoUserId_ThrowsUnauthorizedAccessException` (and `WhenUserNotFound_ThrowsNotFoundException`)
- `RefreshTokenHandler_WhenTokenIsBlank_ReturnsNullWithoutThrowing`
- `LogoutHandler_WhenTokenIsNullOrBlank_CompletesWithoutTouchingDb`

- [ ] **Step 2: Run `WolverineHandlerTests` to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~WolverineHandlerTests"`
Expected: FAIL (new slice namespaces, messages, and handlers do not exist yet).

- [ ] **Step 3: Implement the 6 pure `Features/` slices (`GetHealth`, `Register`, `Login`, `Refresh`, `Logout`, `GetMe`)**

Create the files listed above with `public sealed class <Name>Handler : IHandler` and zero `Microsoft.AspNetCore.*` HTTP/MVC/Routing imports (`Microsoft.AspNetCore.Identity` for `UserManager<ApplicationUser>` is permitted):
- `RegisterHandler`: throw `new FluentValidation.ValidationException([new ValidationFailure("Request", "Email and password are required.") { ErrorCode = "REQUIRED" }])` when email/password blank; throw `ValidationException` with `ValidationFailure(nameof(command.Email), "Email is already registered.") { ErrorCode = "DUPLICATE_EMAIL" }` if existing user found; map `IdentityResult.Errors` to `ValidationFailure(e.Code, e.Description) { ErrorCode = e.Code }` and throw `ValidationException` when `!result.Succeeded`; otherwise create and save `RefreshToken` and return `RegisterResult`.
- `LoginHandler`: throw `new UnauthorizedAccessException("Invalid email or password.")` when blank, user missing, or `!await userManager.CheckPasswordAsync(user, command.Password)`; otherwise persist `RefreshToken` and return `LoginResult`.
- `RefreshTokenHandler`: return `null` when `string.IsNullOrWhiteSpace(command.RefreshToken)`, token missing, or `token.IsExpired(timeProvider)`; when `token.IsRevoked || token.ReplacedByTokenHash != null`, execute `db.RefreshTokens.Where(t => t.UserId == token.UserId && t.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct)` and return `null`; otherwise rotate token, save changes, and return `RefreshTokenResult`.
- `LogoutHandler`: when `!string.IsNullOrWhiteSpace(command.RefreshToken)`, hash token, revoke active matching `RefreshToken`, and save changes.
- `GetMeHandler`: throw `new UnauthorizedAccessException("User is not authenticated.")` when `userContext.UserId is not { } userId`; throw `new NotFoundException($"User '{userId}' was not found.")` when user missing; return `new GetMeResult(user.Id, user.Email!)`.
- `GetHealthHandler`: call `await healthCheckService.CheckHealthAsync(ct)` and return `new GetHealthResult(report.Status.ToString(), DateTime.UtcNow, report.Status == HealthStatus.Healthy)`.

- [ ] **Step 4: Run `WolverineHandlerTests` to verify all pure handler unit tests pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~WolverineHandlerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Features/Health/GetHealth apps/backend/src/Features/Auth/Register apps/backend/src/Features/Auth/Login apps/backend/src/Features/Auth/Refresh apps/backend/src/Features/Auth/Logout apps/backend/src/Features/Auth/GetMe apps/backend/tests/ExamPrism.Tests/Features/WolverineHandlerTests.cs
git commit -m "feat(features): add transport-agnostic IHandler slices and shared Command/Query/Result contracts"
```

---

### Task 3: `Endpoints/` HTTP Layer, `Program.cs` Cutover, `WolverineFx.Http` Removal & Test Suite Migration

**Files:**
- Move: `apps/backend/src/Features/Auth/AuthCookieHelper.cs` -> `apps/backend/src/Endpoints/Auth/AuthCookieHelper.cs`
- Create: `apps/backend/src/Endpoints/Auth/RegisterEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Auth/LoginEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Auth/RefreshEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Auth/LogoutEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Auth/GetMeEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Health/GetHealthEndpoint.cs`
- Create: `apps/backend/src/Endpoints/Test/TestExceptionEndpoints.cs`
- Create: `apps/backend/src/Endpoints/Test/TestValidationEndpoints.cs`
- Delete: `apps/backend/src/Features/Auth/RegisterHandler.cs`
- Delete: `apps/backend/src/Features/Auth/LoginHandler.cs`
- Delete: `apps/backend/src/Features/Auth/RefreshHandler.cs`
- Delete: `apps/backend/src/Features/Auth/LogoutHandler.cs`
- Delete: `apps/backend/src/Features/Auth/GetMeHandler.cs`
- Delete: `apps/backend/src/Features/Health/HealthHandler.cs`
- Delete: `apps/backend/src/Features/Test/TestExceptionHandlers.cs`
- Delete: `apps/backend/src/Features/Test/TestValidationHandlers.cs`
- Delete: `apps/backend/src/Common/Validation/CustomProblemDetailSource.cs`
- Modify: `apps/backend/Directory.Packages.props`
- Modify: `apps/backend/src/ExamPrism.csproj`
- Modify: `apps/backend/src/Program.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Features/AuthCookieHelperTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Features/AuthEndpointTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/HealthEndpointTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DatabaseIntegrationTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/ValidationFilterTests.cs`

**Interfaces:**
- Consumes: `Common.Endpoints.IEndpoint`, `Common.Endpoints.EndpointExtensions`, `Wolverine.IMessageBus`, and all message/result types produced in Task 2.
- Produces:
  - `ExamPrism.Endpoints.Auth.{AuthCookieHelper, RegisterEndpoint, LoginEndpoint, RefreshEndpoint, LogoutEndpoint, GetMeEndpoint}`
  - `ExamPrism.Endpoints.Health.GetHealthEndpoint`
  - `ExamPrism.Endpoints.Test.{TestExceptionEndpoints, TestValidationEndpoints}`

- [ ] **Step 1: Relocate `AuthCookieHelper.cs` via `git mv` and create all `IEndpoint` implementations under `apps/backend/src/Endpoints/`**

- `git mv apps/backend/src/Features/Auth/AuthCookieHelper.cs apps/backend/src/Endpoints/Auth/AuthCookieHelper.cs` and update its namespace to `ExamPrism.Endpoints.Auth`.
- Implement `public sealed class ... : IEndpoint` for:
  - `RegisterEndpoint` (`POST /api/auth/register`, `WithTags("Authentication")`, invokes `bus.InvokeAsync<RegisterResult>(command, ct)`, calls `AuthCookieHelper.SetAuthCookies`, returns `Results.Created("/api/auth/me", result)`).
  - `LoginEndpoint` (`POST /api/auth/login`, `WithTags("Authentication")`, invokes `bus.InvokeAsync<LoginResult>(command, ct)`, sets cookies, returns `Results.Ok(result)`).
  - `RefreshEndpoint` (`POST /api/auth/refresh`, `WithTags("Authentication")`, reads `AuthCookieHelper.RefreshTokenCookieName` cookie — returning `Results.Unauthorized()` if missing/blank — invokes `bus.InvokeAsync<RefreshTokenResult?>(new RefreshTokenCommand(rawRefresh), ct)`, clears cookies + `Results.Unauthorized()` when `null`, or sets cookies + `Results.Ok()`).
  - `LogoutEndpoint` (`POST /api/auth/logout`, `WithTags("Authentication")`, reads optional cookie, invokes `bus.InvokeAsync(new LogoutCommand(rawRefresh), ct)`, clears cookies, returns `Results.NoContent()`).
  - `GetMeEndpoint` (`GET /api/auth/me`, `.RequireAuthorization().WithTags("Authentication")`, invokes `bus.InvokeAsync<GetMeResult>(new GetMeQuery(), ct)`, returns `Results.Ok(result)`).
  - `GetHealthEndpoint` (`GET /health`, `WithTags("Health")`, invokes `bus.InvokeAsync<GetHealthResult>(new GetHealthQuery(), ct)`, returns `result.IsHealthy ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)`).
  - `TestExceptionEndpoints` & `TestValidationEndpoints` in `namespace ExamPrism.Endpoints.Test`: port all `/api/test/exception/*` and `/api/test/validation*` Minimal API routes (`TestValidationRequest`, `TestValidationRequestValidator`, `UnvalidatedRequest`, `[SkipValidation]` on `PostSkip`).

- [ ] **Step 2: Delete legacy combined handlers & `CustomProblemDetailSource.cs`, remove `WolverineFx.Http*` packages, and update `Program.cs`**

- Delete the 8 legacy handler files under `Features/Auth/*.cs`, `Features/Health/HealthHandler.cs`, `Features/Test/*.cs`, and `apps/backend/src/Common/Validation/CustomProblemDetailSource.cs`.
- Remove `WolverineFx.Http` and `WolverineFx.Http.FluentValidation` from `apps/backend/Directory.Packages.props` and `apps/backend/src/ExamPrism.csproj`.
- Update `apps/backend/src/Program.cs`:
  - Replace `Wolverine.Http` / `Wolverine.Http.FluentValidation` imports with `Common.Endpoints` and `Wolverine.FluentValidation`.
  - In `builder.Host.UseWolverine(opts => ...)`: keep `opts.Discovery.IncludeAssembly(typeof(Program).Assembly);`, add `opts.UseFluentValidation();`, keep `opts.UseEntityFrameworkCoreTransactions();` and `opts.Policies.AutoApplyTransactions();` (remove the old `ExamPrism.Features.Test` exclusion block).
  - Remove `AddTransient(typeof(IProblemDetailSource<>), ...)` and `AddWolverineHttp()`.
  - Replace `app.MapWolverineEndpoints(...)` with `app.MapEndpoints();`.

- [ ] **Step 3: Update imports and DTO names in `AuthCookieHelperTests.cs`, `AuthEndpointTests.cs`, `HealthEndpointTests.cs`, `DatabaseIntegrationTests.cs`, and `ValidationFilterTests.cs`**

- `AuthCookieHelperTests.cs`: `using ExamPrism.Endpoints.Auth;`
- `AuthEndpointTests.cs`: import `ExamPrism.Features.Auth.{Register,Login,GetMe}`, replace `RegisterRequest`/`RegisterResponse` with `RegisterCommand`/`RegisterResult`, `LoginRequest`/`LoginResponse` with `LoginCommand`/`LoginResult`, and `GetMeResponse` with `GetMeResult`.
- `HealthEndpointTests.cs` & `DatabaseIntegrationTests.cs`: import `ExamPrism.Features.Health.GetHealth` and replace `HealthResponse` with `GetHealthResult`.
- `ValidationFilterTests.cs:118`: update `ExamPrism.Features.Test.TestValidationRequest` to `ExamPrism.Endpoints.Test.TestValidationRequest`.

- [ ] **Step 4: Build solution and run all non-Docker unit + web-factory tests**

Run: `dotnet build apps/backend/ExamPrism.slnx && dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName!~AuthEndpointTests&FullyQualifiedName!~DatabaseIntegrationTests&FullyQualifiedName!~HealthEndpointTests&FullyQualifiedName!~PipelineIntegrationTests&FullyQualifiedName!~GlobalExceptionHandlerTests&FullyQualifiedName!~LocalizationTests"`
Expected: `Build succeeded` with `0 Error(s)` and all unit + Scalar/DI/ValidationFilter tests PASS. (Also run full `dotnet test apps/backend/ExamPrism.slnx` if Docker daemon is accessible in the environment.)

- [ ] **Step 5: Commit**

```bash
git add -A apps/backend
git commit -m "refactor(endpoints): separate IEndpoint HTTP layer from Wolverine IHandler slices"
```

---

### Task 4: Architecture Governance Suite (`ArchitectureTests`) & Bus-Level Validation Verification

**Files:**
- Create: `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Features/BusValidationTests.cs`

**Interfaces:**
- Consumes: `Common.Handlers.IHandler`, `Common.Endpoints.IEndpoint`, `Program`, `Wolverine.IMessageBus`, `FluentValidation.AbstractValidator<T>`
- Produces: Automated architectural and multi-channel bus validation regression suites in `ExamPrism.Tests`

- [ ] **Step 1: Write `ArchitectureTests.cs` covering all 4 rule groups from Spec Section 7.1 (plus return-type helper unit checks)**

In `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`, inspect `typeof(Program).Assembly.GetTypes()` (excluding compiler-generated types containing `<`):
1. `Handlers_ImplementingIHandler_MustBePublicSealedInFeaturesAndEndWithHandler` & `ClassesInFeatures_EndingWithHandler_MustImplementIHandler`
2. `Handlers_MustDeclareSinglePublicStaticHandleEntry_WithValidMessageInputAndResultReturn`:
   - Exactly one public static `Handle` or `HandleAsync` method; any other public declared methods (`BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly`, excluding `object` methods) must be in `{ "Load", "LoadAsync", "Before", "BeforeAsync", "After", "AfterAsync", "Validate", "ValidateAsync" }`.
   - First parameter exists and `paramType.Name` ends with `"Command"`, `"Query"`, or `"Event"`.
   - Return type helper `IsValidHandlerReturnType(Type returnType)` accepts `typeof(void)`, `typeof(Task)`, `typeof(ValueTask)`, non-generic type ending with `"Result"`, or `Task<T>` / `ValueTask<T>` where `T.Name` ends with `"Result"` (add a dedicated `[Theory]` verifying `Task<RefreshTokenResult?>` passes while `Task<IResult>` and `Task<string>` fail).
3. `TypesInFeatures_MustNotReferenceAspNetCoreHttpRoutingOrMvc`:
   - Check fields, properties, constructors, and declared methods (parameters + return types, unwrapping generic type arguments) across all types in `ExamPrism.Features..*` against namespaces starting with `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing`, or `Microsoft.AspNetCore.Mvc`.
4. `Endpoints_ImplementingIEndpoint_MustBePublicSealedInEndpointsAndNotInjectAppDbContext` & `ClassesInEndpoints_EndingWithEndpoint_MustImplementIEndpoint`.

- [ ] **Step 2: Write `BusValidationTests.cs` proving `IMessageBus.InvokeAsync` enforces `AbstractValidator<T>` outside HTTP**

In `apps/backend/tests/ExamPrism.Tests/Features/BusValidationTests.cs`, spin up a minimal `Host.CreateDefaultBuilder()` configured with `UseWolverine(opts => { opts.UseFluentValidation(); opts.Discovery.IncludeType<ProbeValidatedCommandHandler>(); })` and `services.AddSingleton<IValidator<ProbeValidatedCommand>, ProbeValidatedCommandValidator>()`:
```csharp
public record ProbeValidatedCommand(string Code);

public sealed class ProbeValidatedCommandValidator : AbstractValidator<ProbeValidatedCommand>
{
    public ProbeValidatedCommandValidator() => RuleFor(x => x.Code).NotEmpty();
}

public sealed class ProbeValidatedCommandHandler : IHandler
{
    public static Task<ProbeValidatedResult> Handle(ProbeValidatedCommand command) =>
        Task.FromResult(new ProbeValidatedResult(command.Code));
}

public record ProbeValidatedResult(string Code);

[Fact]
public async Task InvokeAsync_WhenCommandFailsFluentValidation_ThrowsValidationExceptionWithoutHttp()
{
    // Start host, resolve IMessageBus, invoke ProbeValidatedCommand("")
    // Assert ThrowAsync<FluentValidation.ValidationException>()
    // Invoke ProbeValidatedCommand("OK") -> Assert result.Code == "OK"
}
```

- [ ] **Step 3: Run `ArchitectureTests` and `BusValidationTests`**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ArchitectureTests|FullyQualifiedName~BusValidationTests"`
Expected: PASS (all architectural rules pass against `ExamPrism` assembly and bus-level validation rejects invalid commands).

- [ ] **Step 4: Commit**

```bash
git add apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs apps/backend/tests/ExamPrism.Tests/Features/BusValidationTests.cs
git commit -m "test(architecture): enforce IHandler/IEndpoint layer contracts and bus-level validation"
```
