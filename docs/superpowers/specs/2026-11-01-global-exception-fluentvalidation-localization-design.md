# Design Specification: Global Exception Handling, Automatic FluentValidation, and Localization

**Date:** 2026-11-01  
**Status:** Approved  
**Scope:** Architectural Subsystem  

---

## 1. Context and Goals

ExamPrism backend uses ASP.NET Core 10 Minimal APIs with custom static interface endpoint mapping (`IEndpoint` and `MapEndpoints`). Currently, endpoints manually validate requests or return generic validation problems, and there is no unified exception handling or multi-language localization.

This specification defines the architecture, components, and pipelines for:
1. **Multi-language Localization**: Native `IStringLocalizer<SharedResources>` with `.resx` files, defaulted to Vietnamese (`vi-VN`) with English (`en-US`) support.
2. **Automatic FluentValidation**: Assembly scanning and automated endpoint filter execution for Minimal APIs without requiring manual `.WithValidation<T>()` boilerplate on every route.
3. **Structured RFC 7807 Error Responses**: Standardized problem details for validation errors containing field name, error code, and localized message.
4. **Global Exception Handling**: ASP.NET Core 10 `IExceptionHandler` middleware mapping unhandled exceptions, domain exceptions, and validation failures to standardized RFC 7807 `ProblemDetails`.

---

## 2. Architecture & Tech Stack

* **Platform:** .NET 10 Minimal APIs with `IEndpoint` discovery.
* **Validation:** `FluentValidation` and `FluentValidation.DependencyInjectionExtensions`.
* **Localization:** `Microsoft.Extensions.Localization` with standard `.resx` resource architecture.
* **Diagnostics & Error Handling:** `Microsoft.AspNetCore.Diagnostics.IExceptionHandler` and `ProblemDetails`.
* **Testing:** xUnit, FluentAssertions, `Microsoft.AspNetCore.Mvc.Testing`.

---

## 3. Localization Architecture

### 3.1. Shared Marker Type and Resource Files
To support shared application strings (validation messages, common error descriptions), a marker type is placed in `Common/Localization`:

* `Common/Localization/SharedResources.cs`: Marker class used for `IStringLocalizer<SharedResources>`.
* `Common/Localization/Resources/SharedResources.resx`: Neutral / English fallback strings.
* `Common/Localization/Resources/SharedResources.vi.resx`: Vietnamese localized strings.

Initial localized keys:
* `StudentIdsRequired`:
  * `SharedResources.resx`: `"Student IDs list cannot be empty."`
  * `SharedResources.vi.resx`: `"Danh sách mã sinh viên không được để trống."`
* `DuplicateStudentIds`:
  * `SharedResources.resx`: `"Duplicate student IDs are not allowed."`
  * `SharedResources.vi.resx`: `"Danh sách chứa mã sinh viên trùng lặp."`
* `InternalServerError`:
  * `SharedResources.resx`: `"An unexpected error occurred. Please try again later."`
  * `SharedResources.vi.resx`: `"Đã xảy ra lỗi hệ thống. Vui lòng thử lại sau."`

### 3.2. Service and Middleware Configuration
In `Program.cs`:
```csharp
builder.Services.AddLocalization(options => options.ResourcesPath = "Common/Localization/Resources");

var supportedCultures = new[] { "vi-VN", "vi", "en-US", "en" };
var localizationOptions = new RequestLocalizationOptions()
    .SetDefaultCulture("vi-VN")
    .AddSupportedCultures(supportedCultures)
    .AddSupportedUICultures(supportedCultures);

app.UseRequestLocalization(localizationOptions);
```
`UseRequestLocalization` executes before the exception handler and endpoint pipeline so `CultureInfo.CurrentCulture` and `CultureInfo.CurrentUICulture` are set from `Accept-Language` headers or `?culture=` query params on every incoming request.

---

## 4. FluentValidation & Automatic Endpoint Filter Architecture

### 4.1. Validator Definition Pattern
Validators inherit from `AbstractValidator<T>` and inject `IStringLocalizer<SharedResources>`:
```csharp
public class RemoveStudentsValidator : AbstractValidator<RemoveStudentsRequest>
{
    public RemoveStudentsValidator(IStringLocalizer<SharedResources> localizer)
    {
        RuleFor(x => x.StudentIds)
            .NotEmpty()
            .WithErrorCode("STUDENT_IDS_REQUIRED")
            .WithMessage(_ => localizer["StudentIdsRequired"]);

        RuleFor(x => x.StudentIds)
            .Must(ids => ids == null || ids.Distinct().Count() == ids.Count)
            .WithErrorCode("DUPLICATE_STUDENT_IDS")
            .WithMessage(_ => localizer["DuplicateStudentIds"]);
    }
}
```

### 4.2. Automatic DI Registration
All validators in the application assembly are registered automatically:
```csharp
builder.Services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);
```

### 4.3. Automatic Validation Endpoint Filter Factory
Rather than requiring manual `.AddEndpointFilter<ValidationFilter<T>>()` calls on every endpoint, an `EndpointFilterFactory` analyzes endpoint delegates at route build time:

1. **Build-Time Inspection**:
   In `Common/Validation/ValidationFilterFactory.cs`, the factory inspects parameters of `context.MethodInfo`:
   * Excludes framework and infrastructure types (`HttpContext`, `HttpRequest`, `HttpResponse`, `ClaimsPrincipal`, `CancellationToken`, `IServiceProvider`).
   * For remaining parameters, checks whether `IValidator<T>` is registered in DI.
2. **Zero-Overhead Optimization**:
   * If an endpoint handler has no validatable parameters, the factory returns `next` directly without wrapping the invocation in any filter delegate.
3. **Runtime Execution**:
   * For endpoints with validatable parameters, the filter resolves `IValidator<T>` from `httpContext.RequestServices`.
   * If parameter instance is null (and parameter is not optional), or if `validator.ValidateAsync(instance)` returns invalid:
     * Pipeline execution halts immediately.
     * Returns an HTTP 400 Bad Request with an RFC 7807 response detailing each failure.
4. **Opt-Out Mechanism**:
   * Endpoints decorated with `[SkipValidation]` bypass automatic validation.

### 4.4. Integration into `MapEndpoints`
`Common/Endpoints/EndpointExtensions.cs` maps endpoints through a route group configured with the filter factory:
```csharp
public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder app, params Assembly[] assemblies)
{
    var validationGroup = app.MapGroup("").AddEndpointFilterFactory(ValidationFilterFactory.Create);
    // Discovers and invokes IEndpoint.MapEndpoint on validationGroup
}
```

---

## 5. RFC 7807 Response Formats & Global Exception Handling

### 5.1. Error Response Contract
Validation failures return HTTP 400 with structured `errors` containing `field`, `code`, and `message`:
```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "instance": "/api/students/remove",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "errors": [
    {
      "field": "StudentIds",
      "code": "STUDENT_IDS_REQUIRED",
      "message": "Danh sách mã sinh viên không được để trống."
    }
  ]
}
```

### 5.2. `GlobalExceptionHandler` (`IExceptionHandler`)
Registered via `builder.Services.AddExceptionHandler<GlobalExceptionHandler>()` and `app.UseExceptionHandler()`.

Exception handling rules:
1. **`FluentValidation.ValidationException`**:
   * Status: **400 Bad Request**
   * Body: RFC 7807 ProblemDetails containing formatted validation error list.
2. **`BadHttpRequestException`**:
   * Status: **400 Bad Request**
   * Detail: Parsing / malformed request body explanation.
3. **`NotFoundException` / `KeyNotFoundException`**:
   * Status: **404 Not Found**
   * Title: `"Resource Not Found"`
4. **`UnauthorizedAccessException`**:
   * Status: **401 Unauthorized**
   * Title: `"Unauthorized"`
5. **Unhandled `Exception`**:
   * Status: **500 Internal Server Error**
   * Title: `"Internal Server Error"`
   * Detail:
     * In Development environment: `exception.ToString()`.
     * In Production environment: `"An unexpected error occurred. Please try again later."` (or localized `SharedResources["InternalServerError"]`).
   * Structured logging: `_logger.LogError(exception, "Unhandled exception occurred at {Path} (TraceId: {TraceId})", httpContext.Request.Path, traceId)`.

---

## 6. Project Layout & File Locations

```
apps/backend/
├── Directory.Packages.props                <-- Add FluentValidation packages
├── src/
│   ├── ExamPrism.csproj                     <-- Package references
│   ├── Program.cs                           <-- Pipeline & middleware configuration
│   ├── Common/
│   │   ├── Endpoints/
│   │   │   └── EndpointExtensions.cs        <-- Wrap endpoints in validation group
│   │   ├── Exceptions/
│   │   │   ├── DomainException.cs           <-- Base domain exception
│   │   │   ├── NotFoundException.cs         <-- 404 domain exception
│   │   │   └── GlobalExceptionHandler.cs    <-- IExceptionHandler implementation
│   │   ├── Localization/
│   │   │   ├── SharedResources.cs           <-- Localization marker class
│   │   │   └── Resources/
│   │   │       ├── SharedResources.resx     <-- Neutral / en fallback keys
│   │   │       └── SharedResources.vi.resx  <-- Vietnamese translations
│   │   └── Validation/
│   │       ├── ValidationError.cs           <-- Record(string Field, string Code, string Message)
│   │       ├── ValidationProblemDetails.cs  <-- ProblemDetails payload with errors
│   │       ├── SkipValidationAttribute.cs   <-- Endpoint opt-out attribute
│   │       └── ValidationFilterFactory.cs   <-- Endpoint filter factory
└── tests/
    └── ExamPrism.Tests/
        └── Infrastructure/
            ├── LocalizationTests.cs         <-- Culture switching & translation tests
            ├── ValidationFilterTests.cs     <-- Auto validation & error code tests
            └── GlobalExceptionHandlerTests.cs <-- Exception mapping & ProblemDetails tests
```

---

## 7. Testing & Verification

1. **Localization Verification**:
   * Test requesting with `Accept-Language: vi-VN` returns Vietnamese messages.
   * Test requesting with `Accept-Language: en-US` returns English messages.
   * Test fallback culture defaults to `vi-VN`.
2. **Automatic Validation Verification**:
   * Send invalid request model to endpoint: assert 400 Bad Request, RFC 7807 response schema, correct `code` and `field`.
   * Send valid request model: assert endpoint executes successfully (200 / 201).
   * Request to endpoint without validator: assert transparent execution.
   * Request to endpoint with `[SkipValidation]`: assert validation is skipped.
3. **Global Exception Handler Verification**:
   * Unhandled exception yields 500 status code with `traceId` and masked error details in non-development mode.
   * Custom `NotFoundException` yields 404 status code with ProblemDetails.
4. **Full Regression**:
   * Run `dotnet test` on the entire test solution to verify existing authentication and health endpoints remain unaffected.
