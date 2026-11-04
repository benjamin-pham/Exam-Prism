# Serilog Backend Logging Implementation Plan

> **Goal:** Configure Serilog as backend logging engine for ExamPrism ASP.NET Core API while keeping all application and feature code strictly dependent on `Microsoft.Extensions.Logging.ILogger<T>`.

## Global Constraints
- Target Framework: .NET 10.0 (`net10.0`).
- Central Package Management: all package versions in `apps/backend/Directory.Packages.props`.
- Client/feature code must NOT directly reference Serilog types; use `ILogger<T>` only.
- Multi-tenancy and User identity: `TenantId` and `UserId` must be enriched into log contexts when available.
- Strict TDD: Write failing tests first, verify RED, write minimal code for GREEN, then REFACTOR.

---

# Task 1: Core Serilog Configuration and Host Integration

**Scope & Purpose:**
Configure Serilog centrally in ASP.NET Core host and configure sinks/formatters via configuration files (`appsettings.json` and `appsettings.Development.json`).

**Files:**
- `apps/backend/Directory.Packages.props`
- `apps/backend/src/ExamPrism.csproj`
- `apps/backend/src/appsettings.json`
- `apps/backend/src/appsettings.Development.json`
- `apps/backend/src/Infrastructure/DependencyInjection.cs`
- `apps/backend/tests/ExamPrism.Tests/Infrastructure/Logging/SerilogConfigurationTests.cs`

**Step-by-Step:**
1. RED: Write unit test in `SerilogConfigurationTests.cs` verifying Serilog can be configured from an `IConfiguration` with Console sink and registered with `IServiceCollection` / `IHostBuilder`, and that resolving `ILogger<SerilogConfigurationTests>` succeeds.
2. Verify RED fails because Serilog packages and setup are not yet registered.
3. GREEN:
   - Add `Serilog.AspNetCore` (10.0.0 or 9.0.0) and `Serilog.Formatting.Compact` (3.0.0) to `Directory.Packages.props` and `ExamPrism.csproj`.
   - Update `appsettings.json` to replace standard `"Logging"` section with `"Serilog"` section configured with Console sink and `RenderedCompactJsonFormatter`.
   - Update `appsettings.Development.json` with Serilog Console sink formatted for development readability.
   - Update `DependencyInjection.AddInfrastructure(this WebApplicationBuilder builder)` to call `builder.Host.UseSerilog(...)`.
4. Verify GREEN: Tests in `SerilogConfigurationTests.cs` and existing suite pass.
5. REFACTOR: Clean up any formatting or config redundancy.

---

# Task 2: HTTP Log Enrichment and Request Logging

**Scope & Purpose:**
Enrich HTTP request logging with `TenantId` and `UserId` so any logs produced during the request automatically carry these context values. Add `UseSerilogRequestLogging` for summarized HTTP request diagnostics.

**Files:**
- `apps/backend/src/Infrastructure/Logging/LogEnrichmentMiddleware.cs`
- `apps/backend/src/Infrastructure/DependencyInjection.cs`
- `apps/backend/src/Program.cs`
- `apps/backend/tests/ExamPrism.Tests/Infrastructure/Logging/LogEnrichmentMiddlewareTests.cs`

**Step-by-Step:**
1. RED: Write unit tests in `LogEnrichmentMiddlewareTests.cs` testing `LogEnrichmentMiddleware`:
   - When `ITenantContext.TenantId` and `IUserContext.UserId` are present, `LogContext` is enriched with both properties while invoking the next middleware.
   - When tenant/user context is null or empty, next middleware is still invoked cleanly without throwing.
2. Verify RED fails because `LogEnrichmentMiddleware` does not exist.
3. GREEN:
   - Implement `LogEnrichmentMiddleware` using `LogContext.PushProperty("TenantId", ...)` and `LogContext.PushProperty("UserId", ...)`.
   - In `Program.cs`, add `app.UseMiddleware<LogEnrichmentMiddleware>()` right after `app.UseAuthentication()`.
   - In `Program.cs`, add `app.UseSerilogRequestLogging(options => ...)` attaching `TenantId` and `UserId` to `IDiagnosticContext`.
4. Verify GREEN: All `LogEnrichmentMiddlewareTests` pass.
5. REFACTOR: Ensure proper disposal of `IDisposable` property tokens returned by `LogContext.PushProperty`.

---

# Task 3: Wolverine Message Log Enrichment

**Scope & Purpose:**
Enrich background Wolverine message handling with `TenantId` in `LogContext` so message logs are traceable to their tenant.

**Files:**
- `apps/backend/src/Infrastructure/MultiTenancy/TenantWolverineMiddleware.cs`
- `apps/backend/tests/ExamPrism.Tests/Infrastructure/MultiTenancy/TenantWolverineMiddlewareTests.cs`

**Step-by-Step:**
1. RED: Add unit test to `TenantWolverineMiddlewareTests.cs` verifying that when a message with `TenantId` is handled, the tenant property is pushed to `LogContext` in `BeforeAsync` and disposed/popped in `Finally`.
2. Verify RED fails because `TenantWolverineMiddleware` does not yet push to `LogContext`.
3. GREEN:
   - Update `TenantWolverineMiddleware` to store an `IDisposable? _logContextToken` and push `TenantId` in `BeforeAsync`, then dispose it in `Finally()`.
4. Verify GREEN: All `TenantWolverineMiddlewareTests` and the full test suite pass.
5. REFACTOR: Keep middleware clean, thread-safe, and null-safe.

