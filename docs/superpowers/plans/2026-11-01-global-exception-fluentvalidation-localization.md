# Global Exception Handling, Automatic FluentValidation, and Localization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide automated Minimal API FluentValidation, multi-language localization (Vietnamese/English), and RFC 7807 global exception handling in ASP.NET Core 10.

**Architecture:** Route-group endpoint filter factory inspects Minimal API parameters and validates against registered `IValidator<T>` with zero per-endpoint boilerplate; `IStringLocalizer<SharedResources>` injects into validators to produce localized messages; ASP.NET Core 10 `IExceptionHandler` catches unhandled and domain exceptions and formats them as RFC 7807 `ProblemDetails`.

**Tech Stack:** .NET 10, ASP.NET Core Minimal APIs, FluentValidation (v11+), `Microsoft.Extensions.Localization`, RFC 7807 ProblemDetails, xUnit, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-11-01-global-exception-fluentvalidation-localization-design.md`

## Global Constraints

- Target Framework: `net10.0` with nullable reference types enabled and implicit usings.
- Central Package Management: versions defined in `Directory.Packages.props`, references in `src/ExamPrism.csproj`.
- Default Culture: `vi-VN` (fallback / secondary: `en-US`).
- Error Format: RFC 7807 ProblemDetails format with structured `errors` array (`field`, `code`, `message`).
- Minimal API Pattern: maintain zero endpoint-level boilerplate through `Common/Endpoints/EndpointExtensions.cs`.

## Review Focus

1. Unvalidated endpoints or routes without validators must execute transparently without performance degradation or unnecessary allocations.
2. Endpoint with multiple complex parameters where only one has an `IValidator<T>` must validate only the registered one without throwing.
3. Requests with missing `Accept-Language` headers must default to `vi-VN` localization.
4. Validation failure must return HTTP 400 with `errors` containing `field`, `code`, and localized `message`.
5. Unhandled 500 exceptions in non-Development environments must not expose internal stack traces or database connection details to clients.

---

### Task 1: Multi-language Localization Infrastructure

**Files:**
- Create: `apps/backend/src/Common/Localization/SharedResources.cs`
- Create: `apps/backend/src/Common/Localization/Resources/SharedResources.resx`
- Create: `apps/backend/src/Common/Localization/Resources/SharedResources.vi.resx`
- Modify: `apps/backend/src/Program.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/LocalizationTests.cs`

**Interfaces:**
- Consumes: None (built-in ASP.NET Core localization).
- Produces: `IStringLocalizer<SharedResources>` registered in DI, `app.UseRequestLocalization(...)` in HTTP pipeline.

- [ ] **Step 1: Write the failing localization tests**

Create `apps/backend/tests/ExamPrism.Tests/Infrastructure/LocalizationTests.cs` testing:
1. `IStringLocalizer<SharedResources>` resolves Vietnamese translations when `CultureInfo.CurrentUICulture` is `vi-VN`.
2. Resolves English/neutral translations when `CultureInfo.CurrentUICulture` is `en-US`.
3. Default culture is `vi-VN`.

```csharp
namespace ExamPrism.Tests.Infrastructure;

using System.Globalization;
using Common.Localization;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

public class LocalizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public LocalizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("vi-VN", "Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau.")]
    [InlineData("en-US", "An unexpected error occurred. Please try again later.")]
    public void StringLocalizer_ReturnsCorrectLocalizedMessage_ForCulture(string cultureName, string expected)
    {
        using var scope = _factory.Services.CreateScope();
        var localizer = scope.ServiceProvider.GetRequiredService<IStringLocalizer<SharedResources>>();

        var culture = new CultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var message = localizer["InternalServerError"].Value;

        message.Should().Be(expected);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests --filter LocalizationTests`
Expected: FAIL (compilation error: `SharedResources` does not exist).

- [ ] **Step 3: Implement Localization Components**

1. Create `apps/backend/src/Common/Localization/SharedResources.cs`:
```csharp
namespace Common.Localization;

public class SharedResources
{
}
```
2. Create `apps/backend/src/Common/Localization/Resources/SharedResources.resx` with key `InternalServerError` ("An unexpected error occurred. Please try again later.") and `ValidationErrorOccurred` ("One or more validation errors occurred.").
3. Create `apps/backend/src/Common/Localization/Resources/SharedResources.vi.resx` with key `InternalServerError` ("Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau.") and `ValidationErrorOccurred` ("Một hoặc nhiều lỗi xác thực dữ liệu đã xảy ra.").
4. In `apps/backend/src/Program.cs`:
Add `builder.Services.AddLocalization(options => options.ResourcesPath = "Common/Localization/Resources");`
Add `app.UseRequestLocalization(...)` with `vi-VN` default and `vi-VN`, `en-US` supported.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests --filter LocalizationTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Localization apps/backend/src/Program.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/LocalizationTests.cs
git commit -m "feat(localization): add SharedResources resx files and request localization"
```

---

### Task 2: Automatic FluentValidation & Filter Factory

**Files:**
- Modify: `apps/backend/Directory.Packages.props`
- Modify: `apps/backend/src/ExamPrism.csproj`
- Create: `apps/backend/src/Common/Validation/ValidationError.cs`
- Create: `apps/backend/src/Common/Validation/ValidationProblemResponse.cs`
- Create: `apps/backend/src/Common/Validation/SkipValidationAttribute.cs`
- Create: `apps/backend/src/Common/Validation/ValidationFilterFactory.cs`
- Modify: `apps/backend/src/Common/Endpoints/EndpointExtensions.cs`
- Modify: `apps/backend/src/Program.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/ValidationFilterTests.cs`

**Interfaces:**
- Consumes: `IStringLocalizer<SharedResources>` from Task 1.
- Produces: Automatic endpoint validation filter factory applied to all routes in `MapEndpoints`, `ValidationError`, `ValidationProblemResponse`.

- [ ] **Step 1: Write the failing validation filter tests**

Create `apps/backend/tests/ExamPrism.Tests/Infrastructure/ValidationFilterTests.cs`:
1. Test an endpoint with a registered validator returns 400 Bad Request with field, error code, and localized message when invalid.
2. Test valid payload returns 200/201.
3. Test `Accept-Language: en-US` returns English validation message.
4. Test endpoint with `[SkipValidation]` ignores invalid input.

```csharp
namespace ExamPrism.Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Validation;
using FluentAssertions;
using Xunit;

public class ValidationFilterTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public ValidationFilterTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Post_InvalidRequest_Returns400WithErrorsAndErrorCode()
    {
        var response = await _client.PostAsJsonAsync("/api/test/validation", new { Name = "" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetInt32().Should().Be(400);

        var errors = body.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().Contain(e =>
            e.GetProperty("field").GetString() == "Name" &&
            e.GetProperty("code").GetString() == "NAME_REQUIRED");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests --filter ValidationFilterTests`
Expected: FAIL (types / endpoint do not exist).

- [ ] **Step 3: Implement FluentValidation & Filter Factory**

1. Add `FluentValidation.DependencyInjectionExtensions` (v11.11.0) to `apps/backend/Directory.Packages.props` and `apps/backend/src/ExamPrism.csproj`.
2. Create `apps/backend/src/Common/Validation/ValidationError.cs`:
```csharp
namespace Common.Validation;

public record ValidationError(string Field, string Code, string Message);
```
3. Create `apps/backend/src/Common/Validation/ValidationProblemResponse.cs` modeling RFC 7807 problem details with `IReadOnlyList<ValidationError> Errors`.
4. Create `apps/backend/src/Common/Validation/SkipValidationAttribute.cs`.
5. Create `apps/backend/src/Common/Validation/ValidationFilterFactory.cs`:
   Inspects `EndpointFilterFactoryContext`. If any parameter has an `IValidator<T>`, resolves validator and executes `ValidateAsync`. If invalid, returns `Results.Problem` with `ValidationProblemResponse`.
6. Update `apps/backend/src/Common/Endpoints/EndpointExtensions.cs`:
   Wrap endpoints in a route group with `.AddEndpointFilterFactory(ValidationFilterFactory.Create)`.
7. Register `builder.Services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);` in `Program.cs`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests --filter ValidationFilterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/Directory.Packages.props apps/backend/src apps/backend/tests/ExamPrism.Tests/Infrastructure/ValidationFilterTests.cs
git commit -m "feat(validation): implement automatic Minimal API FluentValidation filter factory"
```

---

### Task 3: Global Exception Handler with RFC 7807 & Domain Exceptions

**Files:**
- Create: `apps/backend/src/Common/Exceptions/DomainException.cs`
- Create: `apps/backend/src/Common/Exceptions/NotFoundException.cs`
- Create: `apps/backend/src/Common/Exceptions/GlobalExceptionHandler.cs`
- Modify: `apps/backend/src/Program.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/GlobalExceptionHandlerTests.cs`

**Interfaces:**
- Consumes: `IStringLocalizer<SharedResources>`, `ValidationProblemResponse`.
- Produces: ASP.NET Core `IExceptionHandler` pipeline handling unhandled exceptions, validation exceptions, and domain exceptions.

- [ ] **Step 1: Write the failing exception handler tests**

Create `apps/backend/tests/ExamPrism.Tests/Infrastructure/GlobalExceptionHandlerTests.cs`:
1. Unhandled `Exception` returns 500 `ProblemDetails` with `traceId`.
2. `NotFoundException` returns 404 `ProblemDetails`.
3. `FluentValidation.ValidationException` returns 400 with structured validation errors.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests --filter GlobalExceptionHandlerTests`
Expected: FAIL.

- [ ] **Step 3: Implement GlobalExceptionHandler and Domain Exceptions**

1. Create `Common/Exceptions/DomainException.cs` and `Common/Exceptions/NotFoundException.cs`.
2. Create `Common/Exceptions/GlobalExceptionHandler.cs` implementing `IExceptionHandler`:
   - Logs exception using structured `ILogger`.
   - Maps `ValidationException` -> 400 with `ValidationProblemResponse`.
   - Maps `NotFoundException` / `KeyNotFoundException` -> 404 `ProblemDetails`.
   - Maps unhandled -> 500 `ProblemDetails` (masks sensitive details in production).
3. In `apps/backend/src/Program.cs`:
   - `builder.Services.AddExceptionHandler<GlobalExceptionHandler>();`
   - `builder.Services.AddProblemDetails();`
   - `app.UseExceptionHandler();`

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests --filter GlobalExceptionHandlerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Common/Exceptions apps/backend/src/Program.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/GlobalExceptionHandlerTests.cs
git commit -m "feat(exceptions): implement GlobalExceptionHandler and domain exceptions"
```

---

### Task 4: Full Pipeline Integration & Regression Suite

**Files:**
- Test: `apps/backend/tests/ExamPrism.Tests/Infrastructure/PipelineIntegrationTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, and 3.
- Produces: Verified end-to-end integration and verified zero regression across existing tests.

- [ ] **Step 1: Write integration tests**

Add `apps/backend/tests/ExamPrism.Tests/Infrastructure/PipelineIntegrationTests.cs` verifying full lifecycle: request localization -> automatic validation -> endpoint handler -> global exception fallback.

- [ ] **Step 2: Run all tests in the solution**

Run: `dotnet test apps/backend/ExamPrism.slnx`
Expected: All existing tests (Auth, Health, Identity, Database) and all new tests PASS.

- [ ] **Step 3: Commit**

```bash
git add apps/backend/tests/ExamPrism.Tests
git commit -m "test: add full integration tests for validation, localization, and error handling"
```
