# Single-File Constructor-Injected Handlers, Inline Endpoints & Result Pattern Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor all vertical-slice handlers to constructor-injected single-file slices returning a built-in `Common.Results.Result` / `Result<T>`, migrate all `IEndpoint` implementations to inline `MapEndpoint` callbacks without separate `Handle` methods, and enforce every boundary via reflection `ArchitectureTests`.

**Architecture:** `Common/Results` defines transport-agnostic `Error`, `ErrorType`, `Result : IWolverineReturnType`, and `Result<TValue> : Result`, paired with `IHandler<in TRequest>` and `IHandler<in TRequest, TResponse>` in `Common/Handlers` so `Handle(TRequest request, CancellationToken ct = default)` is compile-time locked and services are injected solely through primary constructors. Each vertical slice in `Features/Auth/*.cs` and `Features/Health/*.cs` merges its `Command`/`Query` record, `AbstractValidator<T>`, `Result` record, and `*Handler` class into one file, while `Endpoints/` executes `IMessageBus.InvokeAsync` inline inside `MapEndpoint` lambdas and translates failed `Result`s to RFC 7807 `ProblemDetails` / `ValidationProblemResponse` via `ResultExtensions.ToProblemResult(HttpContext)`.

**Tech Stack:** .NET 10 (`net10.0`), C# 13, ASP.NET Core 10 Minimal APIs, WolverineFx 4.8.0, WolverineFx.FluentValidation 4.8.0, WolverineFx.EntityFrameworkCore 4.8.0, FluentValidation 12.2.0, EF Core / Npgsql, xUnit, FluentAssertions, NSubstitute.

**Spec:** `docs/superpowers/specs/2026-11-01-handler-result-pattern-single-file-design.md`

## Global Constraints

- Target framework `.NET 10` (`net10.0`), C# 13; zero new NuGet packages (`Common.Results` is built-in).
- Preserve git history strictly: no `git rebase`, no `--amend`, no squashing of prior commits.
- Every handler in `ExamPrism.Features..*` must be `public sealed class <Action>Handler(...) : IHandler<TRequest>` or `IHandler<TRequest, TResponse>` with non-defaulted constructor dependencies, one `public` instance (`!IsStatic`) `Handle` method taking `(TRequest, CancellationToken ct = default)` where `TRequest` is a C# `record` ending with `Command`, `Query`, or `Event`, and returning `Task<Result>` or `Task<Result<T>>` (`T` ending with `Result`).
- `ExamPrism.Features..*` must have zero references to `Microsoft.AspNetCore.Http`, `Microsoft.AspNetCore.Routing`, or `Microsoft.AspNetCore.Mvc`.
- Every `IEndpoint` in `ExamPrism.Endpoints..*` must execute route callbacks inline inside `MapEndpoint(IEndpointRouteBuilder app)`, declare zero `Handle` / `HandleAsync` methods, and never inject `DbContext` (neither on the class nor inside nested lambda closure types).
- Preserve RFC 7807 `ProblemDetails` / `ValidationProblemResponse` JSON schema (`traceId`, `errorCode`, localized `vi-VN`/`en-US` `ValidationErrorOccurred` title, `errors: [{ field, code, message }]`) and `HttpOnly` auth cookie semantics.

## Review Focus

1. **Wolverine cascading-message failure on non-generic `IMessageBus.InvokeAsync(command)`**: `Result` must implement `Wolverine.IWolverineReturnType` so executing a `Task<Result>` handler via `await bus.InvokeAsync(command)` (fire-and-wait or scheduled job without `<Result>`) does not fail attempting to route `Result` as a cascaded outgoing message (tested in Task 1).
2. **Empty `errors: []` array when `ErrorType.Validation` has null/empty `ValidationErrors`**: `ResultExtensions.ToProblemResult` must synthesize `[new ValidationError(string.Empty, error.Code, error.Message)]` whenever `Error.ValidationErrors` is `null` or empty, and localize `Title` via `IStringLocalizer<SharedResources>` (tested in Task 1).
3. **Wolverine `AutoApplyTransactions()` skipping `AppDbContext` if constructor parameters have `= null` defaults**: Handler constructors must declare required `AppDbContext db` (without optional `= null` default values) so Wolverine's EF Core transaction middleware binds `AppDbContext` and commits refresh-token compromise revocation when returning `Error.Unauthorized` (tested in Task 3 and enforced in Task 4).
4. **`DbContext` sneaked into an inline `MapEndpoint` lambda closure (`<>c`)**: Because C# compiles inline lambdas into nested closure classes, `ArchitectureTests` must recursively inspect `GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)` fields, constructors, and methods for `DbContext` (tested in Task 4).
5. **Empty/null `/api/auth/login` HTTP payload status (`400`) vs direct `LoginHandler.Handle` call (`ErrorType.Unauthorized`)**: With `LoginCommandValidator` active, malformed/empty HTTP requests to `/api/auth/login` return `400 Bad Request` via `ValidationFilterFactory`, whereas calling `LoginHandler.Handle` directly in unit tests returns a failed `Result` with `ErrorType.Unauthorized` (tested in Task 3).

---

### Task 1: Built-In `Common.Results`, Generic `IHandler` Contracts & HTTP `ResultExtensions`

**Files:**
- Create: `apps/backend/src/Common/Results/Error.cs`
- Create: `apps/backend/src/Common/Results/Result.cs`
- Modify: `apps/backend/src/Common/Handlers/IHandler.cs:1-11`
- Create: `apps/backend/src/Common/Endpoints/ResultExtensions.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Common/ResultTests.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Common/ResultExtensionsTests.cs`

**Interfaces:**
- Consumes: `Wolverine.IWolverineHandler`, `Wolverine.IWolverineReturnType`, `Common.Validation.ValidationError`, `Common.Validation.ValidationProblemResponse`, `Common.Localization.SharedResources`
- Produces:
  - `Common.Results.ErrorType` (`None`, `Failure`, `Validation`, `Unauthorized`, `Forbidden`, `NotFound`, `Conflict`, `Unavailable`)
  - `Common.Results.Error(string Code, string Message, ErrorType Type = ErrorType.Failure, IReadOnlyList<ValidationError>? ValidationErrors = null)` + static factories `None`, `Failure`, `Unauthorized`, `Forbidden`, `NotFound`, `Conflict`, `Unavailable`, `Validation(IReadOnlyList<ValidationError>, string)`, `Validation(string field, string code, string message)`
  - `Common.Results.Result : IWolverineReturnType` (`IsSuccess`, `IsFailure`, `Error`, `Success()`, `Failure(Error)`, `Success<TValue>(TValue)`, `Failure<TValue>(Error)`, `implicit operator Result(Error)`)
  - `Common.Results.Result<TValue> : Result` (`Value`, `implicit operator Result<TValue>(TValue)`, `implicit operator Result<TValue>(Error)`)
  - `Common.Handlers.IHandler<in TRequest> : IHandler` (`Task<Result> Handle(TRequest request, CancellationToken ct = default)`)
  - `Common.Handlers.IHandler<in TRequest, TResponse> : IHandler` (`Task<Result<TResponse>> Handle(TRequest request, CancellationToken ct = default)`)
  - `Common.Endpoints.ResultExtensions.ToProblemResult(this Result result, HttpContext httpContext) -> IResult`

- [ ] **Step 1: Write failing unit tests in `apps/backend/tests/ExamPrism.Tests/Common/ResultTests.cs` and `apps/backend/tests/ExamPrism.Tests/Common/ResultExtensionsTests.cs`**

In `ResultTests.cs` (`namespace ExamPrism.Tests.Common`), write tests covering:
- `Success_And_Failure_Invariants_Hold_And_InvalidCombinationsThrow`: `Result.Success()` has `IsSuccess == true`, `Error == Error.None`; `Result.Failure(Error.None)` throws `ArgumentException`; `Result.Success<string>(null!)` throws `ArgumentNullException`; accessing `.Value` on `Result.Failure<string>(Error.NotFound("NF", "Missing"))` throws `InvalidOperationException`.
- `ImplicitOperators_ConvertValueAndErrorCleanly`: `Result<int> ok = 42;` (`ok.IsSuccess == true`, `ok.Value == 42`); `Result<int> fail = Error.Conflict("DUP", "Exists");` (`fail.IsFailure == true`, `fail.Error.Type == ErrorType.Conflict`); `Result nonGenericFail = Error.Unauthorized("UNAUTH", "Denied");` (`nonGenericFail.IsFailure == true`).
- `NonGenericResult_ImplementsIWolverineReturnType_AndDoesNotCascadeOnNonGenericInvokeAsync`: build a minimal Wolverine `IHost` (`DisableConventionalDiscovery()`, `IncludeType<ProbeVoidCommandHandler>()` implementing `IHandler<ProbeVoidCommand>` returning `Task.FromResult(Result.Success())`) and assert `await bus.InvokeAsync(new ProbeVoidCommand())` completes without throwing a Wolverine indeterminate-route exception.

In `ResultExtensionsTests.cs` (`namespace ExamPrism.Tests.Common`), execute returned `IResult` against a `DefaultHttpContext` with `MemoryStream` body + `ServiceCollection` (`AddLogging()`, `AddProblemDetails()`, `AddLocalization(o => o.ResourcesPath = "Common/Localization/Resources")`) and test:
- `ToProblemResult_OnSuccessResult_ThrowsInvalidOperationException`.
- `ToProblemResult_MapsEachNonValidationErrorType_ToExpectedStatusAndProblemDetails` (`Theory` over `Unauthorized -> 401`, `Forbidden -> 403`, `NotFound -> 404`, `Conflict -> 409`, `Unavailable -> 503`, `Failure -> 400`, asserting `status`, `detail`, `traceId`, and `errorCode`).
- `ToProblemResult_ValidationWithNullErrors_SynthesizesFallbackError_AndLocalizesTitle` (`Theory` over `("vi-VN", "Một hoặc nhiều lỗi xác thực dữ liệu đã xảy ra.")` and `("en-US", "One or more validation errors occurred.")` for both explicit `Error.Validation("Email", "DUPLICATE_EMAIL", "Taken")` and `new Error("VAL_CODE", "Fallback msg", ErrorType.Validation, null)`).

- [ ] **Step 2: Run `ResultTests` and `ResultExtensionsTests` to verify they fail**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ExamPrism.Tests.Common.Result"`
Expected: FAIL (`Common.Results` and `ResultExtensions` do not exist).

- [ ] **Step 3: Implement `Error.cs`, `Result.cs`, `IHandler.cs`, and `ResultExtensions.cs`**

- Implement `apps/backend/src/Common/Results/Error.cs`, `apps/backend/src/Common/Results/Result.cs`, and `apps/backend/src/Common/Handlers/IHandler.cs` with the exact signatures and invariants in Spec §2.1–§2.3 (keeping root `public interface IHandler : IWolverineHandler;` so not-yet-migrated handlers still compile).
- Implement `apps/backend/src/Common/Endpoints/ResultExtensions.cs` per Spec §2.4:
  - Guard `ArgumentNullException.ThrowIfNull(result)` and `ArgumentNullException.ThrowIfNull(httpContext)`; throw `InvalidOperationException("Cannot convert a successful Result to a ProblemDetails response.")` when `result.IsSuccess`.
  - Compute `traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier` and `instance = httpContext.Request.Path.Value ?? string.Empty`.
  - Map `ErrorType.Validation` to `Results.Problem(new ValidationProblemResponse { Status = 400, Title = localizedTitle, Instance = instance, TraceId = traceId, Errors = errors })` and non-validation types to `Results.Problem(problemDetails)` with `Extensions["traceId"]` and non-empty `Extensions["errorCode"]`.

- [ ] **Step 4: Run `ResultTests` and `ResultExtensionsTests` to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ExamPrism.Tests.Common.Result"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Results/Error.cs apps/backend/src/Common/Results/Result.cs apps/backend/src/Common/Handlers/IHandler.cs apps/backend/src/Common/Endpoints/ResultExtensions.cs apps/backend/tests/ExamPrism.Tests/Common/ResultTests.cs apps/backend/tests/ExamPrism.Tests/Common/ResultExtensionsTests.cs
git commit -m "feat(common): add built-in Result pattern, generic IHandler contracts, and RFC 7807 ResultExtensions"
```

---

### Task 2: Migrate `Health` Slice to Single-File Constructor-Injected Handler & Inline `GetHealthEndpoint`

**Files:**
- Create: `apps/backend/src/Features/Health/GetHealthHandler.cs`
- Delete: `apps/backend/src/Features/Health/GetHealth/GetHealthHandler.cs`
- Delete: `apps/backend/src/Features/Health/GetHealth/GetHealthQuery.cs`
- Modify: `apps/backend/src/Endpoints/Health/GetHealthEndpoint.cs:1-29`
- Modify: `apps/backend/tests/ExamPrism.Tests/Features/WolverineHandlerTests.cs:11,87-125`
- Modify: `apps/backend/tests/ExamPrism.Tests/HealthEndpointTests.cs:3`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DatabaseIntegrationTests.cs:5`
- Modify: `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs:8`

**Interfaces:**
- Consumes: `Common.Handlers.IHandler<TRequest, TResponse>`, `Common.Results.Result<TValue>`, `Common.Endpoints.ResultExtensions.ToProblemResult`
- Produces:
  - `ExamPrism.Features.Health.GetHealthQuery` (`public record GetHealthQuery;`)
  - `ExamPrism.Features.Health.GetHealthResult` (`public record GetHealthResult(string Status, DateTime Timestamp, [property: JsonIgnore] bool IsHealthy = true);`)
  - `ExamPrism.Features.Health.GetHealthHandler(HealthCheckService healthCheckService) : IHandler<GetHealthQuery, GetHealthResult>`
  - `ExamPrism.Endpoints.Health.GetHealthEndpoint : IEndpoint` (inline `MapGet("/health", ...)` callback, zero `Handle` method)

- [ ] **Step 1: Update `GetHealthHandler` unit tests in `WolverineHandlerTests.cs` to constructor injection and `Result<GetHealthResult>`**

In `apps/backend/tests/ExamPrism.Tests/Features/WolverineHandlerTests.cs`, replace `using ExamPrism.Features.Health.GetHealth;` with `using ExamPrism.Features.Health;` (also update `using` in `HealthEndpointTests.cs`, `DatabaseIntegrationTests.cs`, and `ArchitectureTests.cs`), and rewrite the two health tests to instantiate `var handler = new GetHealthHandler(healthCheckService); var result = await handler.Handle(new GetHealthQuery());`, asserting `result.IsSuccess.Should().BeTrue()`, `result.Value.Status`, `result.Value.IsHealthy`, and `JsonSerializer.Serialize(result.Value)` omitting `IsHealthy`.

- [ ] **Step 2: Run health handler unit tests to verify compilation failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~GetHealthHandler"`
Expected: FAIL (`ExamPrism.Features.Health.GetHealthHandler` not defined yet).

- [ ] **Step 3: Implement single-file `Features/Health/GetHealthHandler.cs`, remove `Features/Health/GetHealth/`, and inline `GetHealthEndpoint.cs`**

- Create `apps/backend/src/Features/Health/GetHealthHandler.cs` (`namespace ExamPrism.Features.Health`) containing `GetHealthQuery`, `GetHealthResult`, and `public sealed class GetHealthHandler(HealthCheckService healthCheckService) : IHandler<GetHealthQuery, GetHealthResult>` whose `Handle(GetHealthQuery query, CancellationToken ct = default)` returns `new GetHealthResult(report.Status.ToString(), DateTime.UtcNow, report.Status == HealthStatus.Healthy)`.
- Delete `apps/backend/src/Features/Health/GetHealth/GetHealthHandler.cs` and `apps/backend/src/Features/Health/GetHealth/GetHealthQuery.cs`.
- Rewrite `apps/backend/src/Endpoints/Health/GetHealthEndpoint.cs` to remove `private static Handle` and map `/health` directly with an inline lambda `(IMessageBus bus, HttpContext httpContext, CancellationToken ct)` invoking `bus.InvokeAsync<Result<GetHealthResult>>(new GetHealthQuery(), ct)`, returning `result.ToProblemResult(httpContext)` when `result.IsFailure`, otherwise `result.Value.IsHealthy ? Results.Ok(result.Value) : Results.Json(result.Value, statusCode: StatusCodes.Status503ServiceUnavailable)`.

- [ ] **Step 4: Run `GetHealthHandler` unit tests to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~GetHealthHandler"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A apps/backend/src/Features/Health apps/backend/src/Endpoints/Health/GetHealthEndpoint.cs apps/backend/tests/ExamPrism.Tests/Features/WolverineHandlerTests.cs apps/backend/tests/ExamPrism.Tests/HealthEndpointTests.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/DatabaseIntegrationTests.cs apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs
git commit -m "refactor(health): consolidate GetHealth into single-file constructor-DI handler and inline endpoint"
```

---

### Task 3: Migrate `Auth` Vertical Slices to Single-File Constructor-Injected Handlers & Inline `Endpoints/Auth/*`

**Files:**
- Create: `apps/backend/src/Features/Auth/RegisterHandler.cs`
- Create: `apps/backend/src/Features/Auth/LoginHandler.cs`
- Create: `apps/backend/src/Features/Auth/RefreshTokenHandler.cs`
- Create: `apps/backend/src/Features/Auth/LogoutHandler.cs`
- Create: `apps/backend/src/Features/Auth/GetMeHandler.cs`
- Delete: `apps/backend/src/Features/Auth/Register/RegisterCommand.cs`, `RegisterHandler.cs`
- Delete: `apps/backend/src/Features/Auth/Login/LoginCommand.cs`, `LoginHandler.cs`
- Delete: `apps/backend/src/Features/Auth/Refresh/RefreshTokenCommand.cs`, `RefreshTokenHandler.cs`
- Delete: `apps/backend/src/Features/Auth/Logout/LogoutCommand.cs`, `LogoutHandler.cs`
- Delete: `apps/backend/src/Features/Auth/GetMe/GetMeQuery.cs`, `GetMeHandler.cs`
- Modify: `apps/backend/src/Endpoints/Auth/RegisterEndpoint.cs:1-34`
- Modify: `apps/backend/src/Endpoints/Auth/LoginEndpoint.cs:1-34`
- Modify: `apps/backend/src/Endpoints/Auth/RefreshEndpoint.cs:1-46`
- Modify: `apps/backend/src/Endpoints/Auth/LogoutEndpoint.cs:1-31`
- Modify: `apps/backend/src/Endpoints/Auth/GetMeEndpoint.cs:1-28`
- Modify: `apps/backend/tests/ExamPrism.Tests/Features/WolverineHandlerTests.cs:1-186`
- Modify: `apps/backend/tests/ExamPrism.Tests/Features/BusValidationTests.cs:1-73`
- Modify: `apps/backend/tests/ExamPrism.Tests/Features/AuthEndpointTests.cs:10-12,253-261`
- Modify: `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs:6-7,108-121`

**Interfaces:**
- Consumes: `Common.Handlers.IHandler<TRequest>`, `Common.Handlers.IHandler<TRequest, TResponse>`, `Common.Results.Result`, `Common.Results.Result<TValue>`, `Common.Results.Error`, `Common.Endpoints.ResultExtensions.ToProblemResult`
- Produces (all in `namespace ExamPrism.Features.Auth`):
  - `RegisterCommand`, `RegisterCommandValidator`, `RegisterResult`, `RegisterHandler(UserManager<ApplicationUser> userManager, AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions, TimeProvider timeProvider) : IHandler<RegisterCommand, RegisterResult>`
  - `LoginCommand`, `LoginCommandValidator`, `LoginResult`, `LoginHandler(UserManager<ApplicationUser> userManager, AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions, TimeProvider timeProvider) : IHandler<LoginCommand, LoginResult>`
  - `RefreshTokenCommand`, `RefreshTokenResult`, `RefreshTokenHandler(AppDbContext db, ITokenService tokenService, IOptions<JwtOptions> jwtOptions, TimeProvider timeProvider) : IHandler<RefreshTokenCommand, RefreshTokenResult>`
  - `LogoutCommand`, `LogoutHandler(AppDbContext db, ITokenService tokenService, TimeProvider timeProvider) : IHandler<LogoutCommand>`
  - `GetMeQuery`, `GetMeResult`, `GetMeHandler(IUserContext userContext, UserManager<ApplicationUser> userManager) : IHandler<GetMeQuery, GetMeResult>`

- [ ] **Step 1: Update `WolverineHandlerTests.cs`, `BusValidationTests.cs`, and `AuthEndpointTests.cs` for `ExamPrism.Features.Auth` and `Result` outcomes**

- In `WolverineHandlerTests.cs`, replace `ExamPrism.Features.Auth.*` sub-namespace imports with `using ExamPrism.Features.Auth;`, construct each handler via `new *Handler(...)` (passing `null!` for `AppDbContext` in non-DB unit tests), call `await handler.Handle(...)`, and assert:
  - `LoginHandler_WhenCredentialsAreEmpty_ReturnsUnauthorizedFailure`: `result.IsFailure.Should().BeTrue()`, `result.Error.Type.Should().Be(ErrorType.Unauthorized)`, `result.Error.Code.Should().Be("INVALID_CREDENTIALS")`.
  - `RegisterHandler_WhenCredentialsAreEmpty_ReturnsValidationFailure`: `result.IsFailure.Should().BeTrue()`, `result.Error.Type.Should().Be(ErrorType.Validation)`, `result.Error.Code.Should().Be("VALIDATION_ERROR")`, `result.Error.ValidationErrors.Should().ContainSingle(e => e.Code == "REQUIRED")`.
  - `RegisterHandler_WhenEmailAlreadyExists_ReturnsDuplicateEmailValidationFailure`: mock `userStore.FindByEmailAsync` / `userManager` returning an existing `ApplicationUser`, assert `result.IsFailure` with `ValidationErrors` containing `Field == "Email"` and `Code == "DUPLICATE_EMAIL"`.
  - `GetMeHandler_WhenUserContextHasNoUserId_ReturnsUnauthorizedFailure` (`ErrorType.Unauthorized`) and `GetMeHandler_WhenUserNotFound_ReturnsNotFoundFailure` (`ErrorType.NotFound`, `Code == "USER_NOT_FOUND"`).
  - `RefreshTokenHandler_WhenTokenIsBlank_ReturnsUnauthorizedFailure` (`ErrorType.Unauthorized`, `Code == "INVALID_REFRESH_TOKEN"`).
  - `LogoutHandler_WhenTokenIsNullOrBlank_ReturnsSuccessResult` (`result.IsSuccess.Should().BeTrue()`).
- In `BusValidationTests.cs`, update `ProbeValidatedCommandHandler` to `: IHandler<ProbeValidatedCommand, ProbeValidatedResult>` returning `Task<Result<ProbeValidatedResult>>` (`bus.InvokeAsync<Result<ProbeValidatedResult>>` returning `validResult.Value.Code == "VALID_CODE"`), and update `ProductionAuthCommands_HaveRegisteredValidators` to `ExamPrism.Features.Auth.RegisterCommand` and `ExamPrism.Features.Auth.LoginCommand`.
- In `AuthEndpointTests.cs` (and `ArchitectureTests.cs` `using` lines), switch to `using ExamPrism.Features.Auth;` and update `Login_WithEmptyOrNullInput_Returns401` to `Login_WithEmptyOrNullInput_Returns400` asserting `HttpStatusCode.BadRequest` per Spec §4.2.

- [ ] **Step 2: Run `WolverineHandlerTests` and `BusValidationTests` to verify they fail**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~WolverineHandlerTests|FullyQualifiedName~BusValidationTests"`
Expected: FAIL (`ExamPrism.Features.Auth` types and constructor-DI handlers do not exist yet).

- [ ] **Step 3: Implement the 5 single-file `Features/Auth/*Handler.cs` slices, delete old `Features/Auth/*/` subdirectories, and inline all 5 `Endpoints/Auth/*Endpoint.cs` callbacks**

- Implement `RegisterHandler.cs`, `LoginHandler.cs`, `RefreshTokenHandler.cs`, `LogoutHandler.cs`, and `GetMeHandler.cs` under `apps/backend/src/Features/Auth/` (`namespace ExamPrism.Features.Auth`) following Spec §3.2 and §4.1–§4.5 (`sealed` validators, non-defaulted constructor parameters, `if (db is not null)` guard before DB access so `null!` unit tests remain fast, returning `Error.*` / `Result.Success()` / implicit `new *Result(...)`).
- Delete the 10 legacy files under `Features/Auth/{Register,Login,Refresh,Logout,GetMe}/`.
- Rewrite `RegisterEndpoint.cs`, `LoginEndpoint.cs`, `RefreshEndpoint.cs`, `LogoutEndpoint.cs`, and `GetMeEndpoint.cs` to remove all `private static Handle` methods and execute `bus.InvokeAsync<Result<...>>` / `bus.InvokeAsync<Result>` directly inside the `app.MapPost` / `app.MapGet` lambda callbacks per Spec §3.3 and §4.1–§4.5.

- [ ] **Step 4: Run `WolverineHandlerTests` and `BusValidationTests` to verify they pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~WolverineHandlerTests|FullyQualifiedName~BusValidationTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A apps/backend/src/Features/Auth apps/backend/src/Endpoints/Auth apps/backend/tests/ExamPrism.Tests/Features apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs
git commit -m "refactor(auth): migrate Auth slices to single-file Result handlers and inline endpoints"
```

---

### Task 4: Tighten `ArchitectureTests` for Generic Instance `IHandler`, `record`-Only `Handle` Signature, `Result` Return Types & Inline `IEndpoint` Callbacks

**Files:**
- Modify: `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs:1-299`

**Interfaces:**
- Consumes: `Common.Handlers.IHandler`, `Common.Handlers.IHandler<TRequest>`, `Common.Handlers.IHandler<TRequest, TResponse>`, `Common.Results.Result`, `Common.Results.Result<TValue>`, `Common.Endpoints.IEndpoint`
- Produces:
  - `ArchitectureTests.Handlers_ImplementingIHandler_MustBePublicSealedInFeaturesEndWithHandlerAndImplementGenericIHandler`
  - `ArchitectureTests.Handlers_MustUseNonDefaultedConstructorInjection_AndDeclareSingleInstanceHandleWithRecordInputAndResultReturn`
  - `ArchitectureTests.IsValidHandlerReturnType(Type returnType) -> bool`
  - `ArchitectureTests.IsRecordType(Type type) -> bool`
  - `ArchitectureTests.Endpoints_ImplementingIEndpoint_MustBeInlineWithoutHandleMethod_AndNeverInjectDbContextIncludingNestedLambdas`

- [ ] **Step 1: Update `ArchitectureTests.cs` with new `IHandler`/`IEndpoint` rules and negative-probe unit tests**

Update `apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs` to verify both `typeof(Program).Assembly` and local negative-probe types in `ExamPrism.Tests.Architecture`:
- `IsValidHandlerReturnType_ValidatesExpectedReturnTypes` (`Theory`):
  - `typeof(Task<Result>)` -> `true`
  - `typeof(Task<Result<RegisterResult>>)` -> `true`
  - `typeof(Task<Result<GetHealthResult>>)` -> `true`
  - `typeof(Task)` -> `false`
  - `typeof(void)` -> `false`
  - `typeof(Task<RegisterResult>)` -> `false` (unwrapped DTO not wrapped in `Common.Results.Result<T>`)
  - `typeof(Task<Result<string>>)` -> `false` (`T` does not end with `Result`)
  - `typeof(Task<Result<IResult>>)` -> `false`
- `IsRecordType_DetectsRecordsVersusClasses` (`Theory`): `typeof(RegisterCommand) -> true`, `typeof(GetMeQuery) -> true`, `typeof(RegisterHandler) -> false`, `typeof(string) -> false` (checked via `type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null`).
- `HasDbContextReference_DetectsDbContextInsideNestedInlineLambdaClosure` (`Fact`): define a private probe class in `ArchitectureTests` whose method creates a delegate `(AppDbContext db) => db.ToString()`, and assert `HasDbContextReference(typeof(ProbeEndpointWithLambdaDbContext)).Should().BeTrue()` so nested `<>c` inspection is proven to catch inline lambda violations.
- Production assembly (`AppAssembly`) rules:
  1. Every `IHandler` in `AppAssembly` is `public`, `sealed`, in `ExamPrism.Features..*`, ends with `Handler`, implements `IHandler<>` or `IHandler<,>`, and every constructor parameter has `!param.HasDefaultValue` (preventing `= null` defaults that break Wolverine `AutoApplyTransactions()`).
  2. Every `IHandler` declares **exactly one** `public` method (`BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly`, `!m.IsSpecialName`), which must be an **instance** method (`!m.IsStatic`) named `"Handle"`, with `parameters.Length is 1 or 2`, `IsRecordType(parameters[0].ParameterType)` and name ending with `Command`/`Query`/`Event`, optional `parameters[1].ParameterType == typeof(CancellationToken)`, and `IsValidHandlerReturnType(handleMethod.ReturnType) == true`.
  3. Keep `TypesInFeatures_MustNotReferenceAspNetCoreHttpRoutingOrMvc`.
  4. Every `IEndpoint` in `AppAssembly` declares **zero** methods named `"Handle"` or `"HandleAsync"` (`BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly`), and `HasDbContextReference(ep)` (recursively inspecting `ep` plus all `GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)` fields, constructors, and methods) is `false`.

- [ ] **Step 2: Run `ArchitectureTests` to verify all production and negative-probe assertions pass**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~ArchitectureTests"`
Expected: PASS (if any handler or endpoint violated a rule in Tasks 2–3, fix the violation and re-run until green).

- [ ] **Step 3: Run full non-Docker unit test suite (and full integration suite if Docker daemon is reachable)**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName!~EndpointTests&FullyQualifiedName!~IntegrationTests&FullyQualifiedName!~ValidationFilterTests&FullyQualifiedName!~LocalizationTests&FullyQualifiedName!~GlobalExceptionHandlerTests&FullyQualifiedName!~ScalarApiDocsTests"`
Expected: PASS with 0 errors and 0 warnings.
*(If Docker is running, also run `dotnet test apps/backend/ExamPrism.slnx` and verify all Testcontainers integration tests pass including `AuthEndpointTests`, `HealthEndpointTests`, and `DatabaseIntegrationTests`.)*

- [ ] **Step 4: Commit**

```bash
git add apps/backend/tests/ExamPrism.Tests/Architecture/ArchitectureTests.cs
git commit -m "test(architecture): enforce generic instance IHandler, Result returns, single-file record inputs, and inline IEndpoint callbacks"
```
