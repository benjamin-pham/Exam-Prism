# Design Document: Single-File Constructor-Injected Handlers, Inline Endpoints & Custom Result Pattern

- **Author**: Antigravity Assistant & Engineering Team
- **Date**: 2026-11-01
- **Status**: Draft (Pending Written Spec Review)
- **Target Framework**: .NET 10 (`net10.0`), C# 13

---

## 1. Overview & Motivation

In the previous `IHandler` / `IEndpoint` design (`2026-11-01-handler-endpoint-contracts-design.md`), application handlers used `public static` `Handle` methods with Wolverine method-parameter service injection, split message contracts (`*Command.cs` / `*Query.cs`) into separate files inside single-use subfolders, communicated expected domain failures via thrown exceptions (`ValidationException`, `UnauthorizedAccessException`, `NotFoundException`) or nullable return types (`RefreshTokenResult?`), and delegated route execution inside `IEndpoint` classes to separate `private static Handle(...)` methods.

To improve developer ergonomics, compile-time contract safety, cohesion per vertical slice, and explicit control flow, this design introduces four cohesive architectural refinements:

1. **Constructor-Injected Instance Handlers (`IHandler<TRequest>` / `IHandler<TRequest, TResponse>`)**:
   - Services are injected exclusively via the handler's constructor (C# primary constructors).
   - The `Handle` method is an instance method accepting **only** the message record (`*Command`, `*Query`, or `*Event`) and an optional `CancellationToken ct = default`. No services may be injected as `Handle` method parameters.
2. **Single-File Vertical Slices (Flattened Feature Group Directories)**:
   - Each use-case consolidates its `*Command` / `*Query` record, `AbstractValidator<T>` (when applicable), `*Result` record (when returning data), and `*Handler` class into a single `*Handler.cs` file placed directly under its feature group folder (`Features/Auth/*.cs`, `Features/Health/*.cs`), eliminating redundant one-file subdirectories.
3. **Built-In Transport-Agnostic Result Pattern (`Common.Results`)**:
   - Handlers return `Task<Result>` or `Task<Result<TResponse>>` (implementing `Wolverine.IWolverineReturnType` so Wolverine never misinterprets `Result` as an outgoing cascading message during background/scheduled execution).
   - Expected business failures (validation, unauthorized, forbidden, not found, conflict, service unavailable) are returned explicitly via `Error` / `ErrorType` rather than thrown exceptions or `null` returns, allowing Wolverine's `AutoApplyTransactions()` middleware to commit intentional state updates (such as refresh token reuse revocation) cleanly. Unhandled infrastructure exceptions continue flowing to `GlobalExceptionHandler` (`500 Internal Server Error`).
4. **Inline Route Callbacks in `IEndpoint.MapEndpoint`**:
   - `IEndpoint` implementations execute `IMessageBus.InvokeAsync<...>()` and HTTP response mapping directly inside the `app.MapGet` / `app.MapPost` callback lambda in `MapEndpoint`, prohibiting separate `Handle` / `HandleAsync` methods inside endpoint files.

---

## 2. Core Contracts

### 2.1 Error & ErrorType (`apps/backend/src/Common/Results/Error.cs`)

`Common.Results` has zero dependency on `Microsoft.AspNetCore.*` and reuses `Common.Validation.ValidationError` (`record ValidationError(string Field, string Code, string Message)`) for field-level validation failures.

```csharp
namespace Common.Results;

using Common.Validation;

public enum ErrorType
{
    None = 0,
    Failure,
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Unavailable
}

public sealed record Error(
    string Code,
    string Message,
    ErrorType Type = ErrorType.Failure,
    IReadOnlyList<ValidationError>? ValidationErrors = null)
{
    public static readonly Error None = new(string.Empty, string.Empty, ErrorType.None);

    public static Error Failure(string code, string message) =>
        new(code, message, ErrorType.Failure);

    public static Error Unauthorized(string code, string message) =>
        new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) =>
        new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) =>
        new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) =>
        new(code, message, ErrorType.Conflict);

    public static Error Unavailable(string code, string message) =>
        new(code, message, ErrorType.Unavailable);

    public static Error Validation(
        IReadOnlyList<ValidationError> errors,
        string message = "One or more validation errors occurred.") =>
        new("VALIDATION_ERROR", message, ErrorType.Validation, errors);

    public static Error Validation(string field, string code, string message) =>
        Validation([new ValidationError(field, code, message)], message);
}
```

### 2.2 `Result` & `Result<TValue>` (`apps/backend/src/Common/Results/Result.cs`)

```csharp
namespace Common.Results;

using Wolverine;

public class Result : IWolverineReturnType
{
    protected Result(bool isSuccess, Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (isSuccess && error != Error.None)
        {
            throw new ArgumentException("Successful result cannot contain an error.", nameof(error));
        }

        if (!isSuccess && error == Error.None)
        {
            throw new ArgumentException("Failed result must contain a non-empty error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Success<TValue>(TValue value) =>
        value is not null
            ? new(value, true, Error.None)
            : throw new ArgumentNullException(nameof(value));

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);

    public static implicit operator Result(Error error) => Failure(error);
}

public sealed class Result<TValue> : Result
{
    private readonly TValue? _value;

    internal Result(TValue? value, bool isSuccess, Error error)
        : base(isSuccess, error)
    {
        _value = value;
    }

    public TValue Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Cannot access Value of a failed Result.");

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}
```

### 2.3 Strongly-Typed Handler Interfaces (`apps/backend/src/Common/Handlers/IHandler.cs`)

```csharp
namespace Common.Handlers;

using Common.Results;
using Wolverine;

/// <summary>
/// Root marker contract for Wolverine discovery and architectural verification.
/// </summary>
public interface IHandler : IWolverineHandler;

/// <summary>
/// Contract for command or event handlers returning a status <see cref="Result"/> without payload.
/// </summary>
public interface IHandler<in TRequest> : IHandler
{
    Task<Result> Handle(TRequest request, CancellationToken ct = default);
}

/// <summary>
/// Contract for command or query handlers returning a typed <see cref="Result{TResponse}"/>.
/// </summary>
public interface IHandler<in TRequest, TResponse> : IHandler
{
    Task<Result<TResponse>> Handle(TRequest request, CancellationToken ct = default);
}
```

### 2.4 HTTP RFC 7807 Mapper (`apps/backend/src/Common/Endpoints/ResultExtensions.cs`)

`ResultExtensions.ToProblemResult(this Result result, HttpContext httpContext)` translates failed `Result` instances into RFC 7807 `ProblemDetails` or `ValidationProblemResponse` payloads matching `GlobalExceptionHandler` and `ValidationFilterFactory`:

- Throws `InvalidOperationException` if called when `result.IsSuccess` is `true`.
- Resolves `traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier` and `instance = httpContext.Request.Path.Value ?? string.Empty`.
- **`ErrorType.Validation` (`400 Bad Request`)**:
  - Resolves localized title from `httpContext.RequestServices.GetService<IStringLocalizer<SharedResources>>()?["ValidationErrorOccurred"].Value ?? "One or more validation errors occurred."`.
  - Populates `ValidationProblemResponse` with `Status = 400`, `Title`, `Instance`, `TraceId`, and `Errors = result.Error.ValidationErrors is { Count: > 0 } ? result.Error.ValidationErrors : [new ValidationError(string.Empty, result.Error.Code, result.Error.Message)]`.
  - Returns `Results.Problem(validationProblem)`.
- **Non-Validation `ErrorType`s**:
  - Maps `(StatusCode, Title, RfcType)`:
    - `ErrorType.Unauthorized` -> `(401, "Unauthorized", "https://tools.ietf.org/html/rfc7235#section-3.1")`
    - `ErrorType.Forbidden` -> `(403, "Forbidden", "https://tools.ietf.org/html/rfc7231#section-6.5.3")`
    - `ErrorType.NotFound` -> `(404, "Resource Not Found", "https://tools.ietf.org/html/rfc7231#section-6.5.4")`
    - `ErrorType.Conflict` -> `(409, "Conflict", "https://tools.ietf.org/html/rfc7231#section-6.5.8")`
    - `ErrorType.Unavailable` -> `(503, "Service Unavailable", "https://tools.ietf.org/html/rfc7231#section-6.6.4")`
    - `ErrorType.Failure` (and fallback) -> `(400, "Domain Error", "https://tools.ietf.org/html/rfc7231#section-6.5.1")`
  - Constructs `ProblemDetails` with `Status`, `Title`, `Detail = result.Error.Message`, `Instance`, `Type`, `Extensions["traceId"] = traceId`, and (when `!string.IsNullOrWhiteSpace(result.Error.Code)`) `Extensions["errorCode"] = result.Error.Code`.
  - Returns `Results.Problem(problemDetails)`.

---

## 3. Directory Layout & File Conventions

### 3.1 Directory Structure

```text
apps/backend/src/
├── Common/
│   ├── Endpoints/
│   │   ├── EndpointExtensions.cs
│   │   ├── IEndpoint.cs
│   │   └── ResultExtensions.cs             # NEW: Result -> RFC 7807 IResult mapper
│   ├── Exceptions/
│   │   ├── DomainException.cs
│   │   ├── GlobalExceptionHandler.cs
│   │   └── NotFoundException.cs
│   ├── Handlers/
│   │   └── IHandler.cs                     # UPDATED: IHandler<TRequest>, IHandler<TRequest, TResponse>
│   ├── Localization/
│   ├── Results/                            # NEW: Built-in Result pattern
│   │   ├── Error.cs
│   │   └── Result.cs
│   ├── Security/
│   └── Validation/
├── Endpoints/                              # HTTP Transport Layer (Inline MapEndpoint lambdas ONLY)
│   ├── Auth/
│   │   ├── AuthCookieHelper.cs
│   │   ├── GetMeEndpoint.cs
│   │   ├── LoginEndpoint.cs
│   │   ├── LogoutEndpoint.cs
│   │   ├── RefreshEndpoint.cs
│   │   └── RegisterEndpoint.cs
│   ├── Health/
│   │   └── GetHealthEndpoint.cs
│   └── Test/
│       ├── TestExceptionEndpoints.cs
│       └── TestValidationEndpoints.cs
└── Features/                               # Flattened Single-File Vertical Slices
    ├── Auth/                               # Namespace: ExamPrism.Features.Auth
    │   ├── GetMeHandler.cs                 # GetMeQuery + GetMeResult + GetMeHandler
    │   ├── LoginHandler.cs                 # LoginCommand + LoginCommandValidator + LoginResult + LoginHandler
    │   ├── LogoutHandler.cs                # LogoutCommand + LogoutHandler
    │   ├── RefreshTokenHandler.cs          # RefreshTokenCommand + RefreshTokenResult + RefreshTokenHandler
    │   └── RegisterHandler.cs              # RegisterCommand + RegisterCommandValidator + RegisterResult + RegisterHandler
    └── Health/                             # Namespace: ExamPrism.Features.Health
        └── GetHealthHandler.cs             # GetHealthQuery + GetHealthResult + GetHealthHandler
```

All previous two-file subdirectories (`Features/Auth/Register/`, `Features/Auth/Login/`, `Features/Auth/Refresh/`, `Features/Auth/Logout/`, `Features/Auth/GetMe/`, `Features/Health/GetHealth/`) are removed.

### 3.2 Single-File Handler Structure Convention

Each `*Handler.cs` file follows a consistent top-to-bottom declaration order:
1. Input message record (`public record <Action>Command(...)` or `public record <Action>Query(...)`)
2. Input validator (`public sealed class <Action>CommandValidator : AbstractValidator<...>`, when applicable)
3. Output data record (`public record <Action>Result(...)`, when returning a value)
4. Handler implementation (`public sealed class <Action>Handler(...) : IHandler<...>`) using a primary constructor for injected dependencies

### 3.3 Inline Endpoint Convention

Every `IEndpoint` class declares only `public static void MapEndpoint(IEndpointRouteBuilder app)` and passes an inline callback lambda directly to `app.MapGet` / `app.MapPost` / `group.Map*`. Separate `Handle` or `HandleAsync` methods on `IEndpoint` classes are forbidden.

Example (`LoginEndpoint.cs`):
```csharp
namespace ExamPrism.Endpoints.Auth;

using Common.Endpoints;
using Common.Results;
using ExamPrism.Features.Auth;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Wolverine;

public sealed class LoginEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", async (
            LoginCommand command,
            IMessageBus bus,
            HttpContext httpContext,
            IOptions<JwtOptions> jwtOptions,
            TimeProvider timeProvider,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<LoginResult>>(command, ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            AuthCookieHelper.SetAuthCookies(
                httpContext.Response,
                result.Value.AccessToken,
                result.Value.RefreshToken,
                jwtOptions.Value,
                timeProvider);

            return Results.Ok(result.Value);
        })
        .WithTags("Authentication")
        .WithSummary("Authenticates user credentials and sets auth cookies");
    }
}
```

---

## 4. Feature Slice Migration Specifications

### 4.1 `Features/Auth/RegisterHandler.cs` & `Endpoints/Auth/RegisterEndpoint.cs`
- **File contents (`ExamPrism.Features.Auth`)**:
  - `RegisterCommand(string Email, string Password)` (preserving `[Required, EmailAddress]` and `[Required, MinLength(6)]` OpenAPI attributes).
  - `RegisterCommandValidator : AbstractValidator<RegisterCommand>`.
  - `RegisterResult(Guid Id, string Email, [property: JsonIgnore] string AccessToken = "", [property: JsonIgnore] string RefreshToken = "")`.
  - `RegisterHandler(UserManager<ApplicationUser> userManager, AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions, TimeProvider timeProvider) : IHandler<RegisterCommand, RegisterResult>`:
    - Uses standard non-defaulted constructor parameters so Wolverine's `AutoApplyTransactions()` reliably binds `AppDbContext` (lightweight unit tests exercising pre-persistence guards may pass `null!` for `db`, guarded by `if (db is not null)` before persistence).
    - Blank email or password guard (when called outside validation pipeline): returns `Error.Validation("Request", "REQUIRED", "Email and password are required.")`.
    - Duplicate email (`FindByEmailAsync` non-null): returns `Error.Validation(nameof(command.Email), "DUPLICATE_EMAIL", "Email is already registered.")`.
    - `userManager.CreateAsync` failure: maps `IdentityError` items into `ValidationError(e.Code, e.Code, e.Description)` and returns `Error.Validation(errors)`.
    - Success: generates access + refresh tokens, persists `RefreshToken` when `db is not null`, and returns `new RegisterResult(user.Id, user.Email!, accessToken, rawRefresh)`.
- **Endpoint (`POST /api/auth/register`)**:
  - Inline lambda invokes `bus.InvokeAsync<Result<RegisterResult>>(command, ct)`.
  - On `IsFailure`: returns `result.ToProblemResult(httpContext)`.
  - On `IsSuccess`: sets auth cookies via `AuthCookieHelper.SetAuthCookies` and returns `Results.Created("/api/auth/me", result.Value)`.

### 4.2 `Features/Auth/LoginHandler.cs` & `Endpoints/Auth/LoginEndpoint.cs`
- **File contents (`ExamPrism.Features.Auth`)**:
  - `LoginCommand(string Email, string Password)`.
  - `LoginCommandValidator : AbstractValidator<LoginCommand>`.
  - `LoginResult(Guid Id, string Email, [property: JsonIgnore] string AccessToken = "", [property: JsonIgnore] string RefreshToken = "")`.
  - `LoginHandler(UserManager<ApplicationUser> userManager, AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions, TimeProvider timeProvider) : IHandler<LoginCommand, LoginResult>`:
    - Blank credentials (direct handler call) or unknown user / password mismatch: returns `Error.Unauthorized("INVALID_CREDENTIALS", "Invalid email or password.")`.
    - Success: generates tokens, persists `RefreshToken` when `db is not null`, and returns `new LoginResult(user.Id, user.Email!, accessToken, rawRefresh)`.
- **Validation & HTTP Status Semantics (`POST /api/auth/login`)**:
  - Because `LoginCommandValidator` is registered in DI, malformed/empty HTTP requests to `/api/auth/login` fail input validation in `ValidationFilterFactory` with `400 Bad Request` (`ValidationProblemResponse`), matching `/api/auth/register` and bus-level validation (`BusValidationTests`). Well-formed requests with wrong credentials reach `LoginHandler` and return `401 Unauthorized` via `Error.Unauthorized`.

### 4.3 `Features/Auth/RefreshTokenHandler.cs` & `Endpoints/Auth/RefreshEndpoint.cs`
- **File contents (`ExamPrism.Features.Auth`)**:
  - `RefreshTokenCommand(string RefreshToken)`.
  - `RefreshTokenResult([property: JsonIgnore] string AccessToken = "", [property: JsonIgnore] string RefreshToken = "")`.
  - `RefreshTokenHandler(AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions, TimeProvider timeProvider) : IHandler<RefreshTokenCommand, RefreshTokenResult>`:
    - Blank token, null `db`, unknown hash, or expired token: returns `Error.Unauthorized("INVALID_REFRESH_TOKEN", "Invalid or expired refresh token.")`.
    - Reused/revoked token (`token.IsRevoked || token.ReplacedByTokenHash != null`): revokes all active tokens for `token.UserId` via `ExecuteUpdateAsync` and returns `Error.Unauthorized("COMPROMISED_REFRESH_TOKEN", "Refresh token has been revoked.")` (Wolverine commits the revocation cleanly because no exception is thrown).
    - Success: rotates token, saves new `RefreshToken`, and returns `new RefreshTokenResult(newAccessToken, newRaw)`.
- **Endpoint (`POST /api/auth/refresh`)**:
  - Inline lambda checks `AuthCookieHelper.RefreshTokenCookieName` cookie; if missing/blank, returns `Results.Unauthorized()`.
  - Invokes `bus.InvokeAsync<Result<RefreshTokenResult>>(new RefreshTokenCommand(rawRefresh), ct)`.
  - On `IsFailure`: calls `AuthCookieHelper.ClearAuthCookies(httpContext.Response)` and returns `result.ToProblemResult(httpContext)` (`401`).
  - On `IsSuccess`: sets new auth cookies and returns `Results.Ok()`.

### 4.4 `Features/Auth/LogoutHandler.cs` & `Endpoints/Auth/LogoutEndpoint.cs`
- **File contents (`ExamPrism.Features.Auth`)**:
  - `LogoutCommand(string? RefreshToken)`.
  - `LogoutHandler(AppDbContext db, ITokenService tokenService, TimeProvider timeProvider) : IHandler<LogoutCommand>`:
    - If `RefreshToken` is non-empty and `db is not null`, hashes token, marks active matching token `RevokedAt = timeProvider.GetUtcNow()`, and saves changes.
    - Returns `Result.Success()`.
- **Endpoint (`POST /api/auth/logout`)**:
  - Inline lambda extracts optional refresh cookie, invokes `bus.InvokeAsync<Result>(new LogoutCommand(rawRefresh), ct)`, clears auth cookies, and returns `result.IsFailure ? result.ToProblemResult(httpContext) : Results.NoContent()`.

### 4.5 `Features/Auth/GetMeHandler.cs` & `Endpoints/Auth/GetMeEndpoint.cs`
- **File contents (`ExamPrism.Features.Auth`)**:
  - `GetMeQuery`.
  - `GetMeResult(Guid Id, string Email)`.
  - `GetMeHandler(IUserContext userContext, UserManager<ApplicationUser> userManager) : IHandler<GetMeQuery, GetMeResult>`:
    - If `userContext.UserId is not { } userId`: returns `Error.Unauthorized("UNAUTHORIZED", "User is not authenticated.")`.
    - If `FindByIdAsync` returns `null`: returns `Error.NotFound("USER_NOT_FOUND", $"User '{userId}' was not found.")`.
    - Success: returns `new GetMeResult(user.Id, user.Email!)`.
- **Endpoint (`GET /api/auth/me`)**:
  - Inline lambda with `.RequireAuthorization()` invokes `bus.InvokeAsync<Result<GetMeResult>>(new GetMeQuery(), ct)` and returns `result.IsFailure ? result.ToProblemResult(httpContext) : Results.Ok(result.Value)`.

### 4.6 `Features/Health/GetHealthHandler.cs` & `Endpoints/Health/GetHealthEndpoint.cs`
- **File contents (`ExamPrism.Features.Health`)**:
  - `GetHealthQuery`.
  - `GetHealthResult(string Status, DateTime Timestamp, [property: JsonIgnore] bool IsHealthy = true)`.
  - `GetHealthHandler(HealthCheckService healthCheckService) : IHandler<GetHealthQuery, GetHealthResult>`:
    - Runs `healthCheckService.CheckHealthAsync(ct)` and returns `new GetHealthResult(report.Status.ToString(), DateTime.UtcNow, report.Status == HealthStatus.Healthy)`.
- **Endpoint (`GET /health`)**:
  - Inline lambda invokes `bus.InvokeAsync<Result<GetHealthResult>>(new GetHealthQuery(), ct)`.
  - If `result.IsFailure`, returns `result.ToProblemResult(httpContext)`.
  - Otherwise returns `result.Value.IsHealthy ? Results.Ok(result.Value) : Results.Json(result.Value, statusCode: StatusCodes.Status503ServiceUnavailable)` (preserving the health status JSON payload on `503`).

---

## 5. Architecture Governance & Testing Strategy

### 5.1 `ArchitectureTests.cs` (`apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs`)

Updated architectural rules enforce all four design invariants automatically:

1. **Handler Identity, Placement & Generic Contract**:
   - Every concrete type implementing `IHandler` must reside in `ExamPrism.Features..*`, be `public` and `sealed`, end with `Handler`, and implement a closed generic `IHandler<TRequest>` or `IHandler<TRequest, TResponse>`.
   - Every concrete class in `ExamPrism.Features..*` ending with `Handler` must implement `IHandler`.
2. **Instance `Handle` Signature & Strict Constructor DI**:
   - Every handler must declare **exactly one** public **instance** (`!m.IsStatic`) method named `Handle` (and zero other public declared methods).
   - `Handle` must accept **1 or 2 parameters only**:
     - Parameter 0: a C# `record` message type (verified via `<Clone>$` synthesis) whose name ends with `Command`, `Query`, or `Event`.
     - Parameter 1 (optional): must be `System.Threading.CancellationToken`.
     - Any service parameter on `Handle` fails the test immediately.
   - `Handle` return type must be `Task<Result>` or `Task<Result<T>>` where `T` is not an `IResult` and `T.Name` ends with `Result`.
3. **HTTP Layer Isolation in `Features/`**:
   - No type in `ExamPrism.Features..*` may reference `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing`, or `Microsoft.AspNetCore.Mvc` in base types, interfaces, fields, properties, constructors, or methods.
4. **Inline `IEndpoint` & Persistence Isolation in `Endpoints/`**:
   - Every `IEndpoint` implementation must reside in `ExamPrism.Endpoints..*`, be `public` and `sealed`, and end with `Endpoint` or `Endpoints`.
   - `IEndpoint` classes must **not** declare any method named `Handle` or `HandleAsync` (enforcing inline route callbacks inside `MapEndpoint`).
   - Neither an `IEndpoint` class nor any of its nested compiler-generated lambda closure types (`GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)`) may declare fields, constructors, or methods taking `DbContext`.

### 5.2 Unit & Integration Tests

1. **`ResultTests.cs` (`ExamPrism.Tests/Common/ResultTests.cs`)**:
   - Verifies `Result.Success()`, `Result.Failure(error)`, `Result<T>.Success(value)`, `Result<T>.Failure(error)`, constructor invariant exceptions, `InvalidOperationException` on accessing `Value` of a failed `Result<T>`, and implicit conversions from `TValue` and `Error`.
2. **`ResultExtensionsTests.cs` (`ExamPrism.Tests/Common/ResultExtensionsTests.cs`)**:
   - Verifies `ToProblemResult(HttpContext)` status codes, RFC 7807 `ProblemDetails` fields (`traceId`, `errorCode`, `instance`, `detail`), `ValidationProblemResponse` mapping for `ErrorType.Validation` (including localized `vi-VN` and `en-US` titles), and `InvalidOperationException` when called on a successful `Result`.
3. **`WolverineHandlerTests.cs`**:
   - Instantiates handlers via constructor injection (`new LoginHandler(...)`, `new RegisterHandler(...)`, etc.), invokes `await handler.Handle(...)`, and asserts `Result` / `Result<T>` outcomes (`IsSuccess`, `IsFailure`, `Error.Type`, `Error.Code`, `Value`) and JSON serialization behavior.
4. **`BusValidationTests.cs`, `AuthEndpointTests.cs`, `HealthEndpointTests.cs`, `AuthCookieHelperTests.cs`**:
   - Updates `using` directives to flattened `ExamPrism.Features.Auth` and `ExamPrism.Features.Health`.
   - Updates `ProbeValidatedCommandHandler` in `BusValidationTests` to `IHandler<ProbeValidatedCommand, ProbeValidatedResult>`.
   - Updates `AuthEndpointTests.Login_WithEmptyOrNullInput_Returns400` to assert `400 Bad Request` consistent with `LoginCommandValidator` and `ValidationFilterFactory`, while retaining `Login_InvalidPassword_Returns401` for `401 Unauthorized` credential verification.
