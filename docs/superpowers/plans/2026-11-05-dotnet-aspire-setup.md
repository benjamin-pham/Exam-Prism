# .NET Aspire AppHost Setup & Configuration Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Set up a root `.NET Aspire` AppHost project (`aspire-app`) registering `apps/backend/src/ExamPrism.csproj` by file path (without `<ProjectReference>`), migrate all non-Serilog configuration from `appsettings*.json` into `aspire-app/Program.cs` environment variables, add `OTEL_EXPORTER_OTLP_ENDPOINT` fallback in `TelemetrySetup`, and keep all backend tests passing.

**Architecture:** `aspire-app/ExamPrism.AppHost.csproj` uses `Aspire.AppHost.Sdk/13.6.1` at the repository root and orchestrates PostgreSQL (`CatalogConnection` -> `examprism_catalog_dev`), Redis (`Redis`), and `examprism-api` (`../apps/backend/src/ExamPrism.csproj`). All 16 non-Serilog configuration sections are injected via `.WithEnvironment(...)` in `aspire-app/Program.cs`, while `apps/backend/src/appsettings.json` and `apps/backend/src/appsettings.Development.json` retain only `Serilog`.

**Tech Stack:** .NET 10 (`net10.0`), .NET Aspire `13.6.1` (`Aspire.AppHost.Sdk`, `Aspire.Hosting.AppHost`, `Aspire.Hosting.PostgreSQL`, `Aspire.Hosting.Redis`), OpenTelemetry, xUnit, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-11-05-dotnet-aspire-setup-design.md`

## Global Constraints

- `aspire-app/ExamPrism.AppHost.csproj` MUST target `net10.0`, use `Aspire.AppHost.Sdk/13.6.1`, and contain zero `<ProjectReference>` items.
- `aspire-app/Program.cs` MUST register backend via `builder.AddProject("examprism-api", "../apps/backend/src/ExamPrism.csproj")`.
- `apps/backend/src/appsettings.json` and `apps/backend/src/appsettings.Development.json` MUST contain only the top-level `"Serilog"` key.
- `TelemetrySetup.AddAppTelemetry` MUST fall back to `configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]` when `Telemetry:OtlpEndpoint` is empty or whitespace.
- Existing tests in `apps/backend/tests/ExamPrism.Tests` MUST pass without relying on non-Serilog sections in `appsettings*.json`.

## Review Focus

- `aspire-app/ExamPrism.AppHost.csproj` accidentally includes `<ProjectReference>` to `ExamPrism.csproj` — test asserts XML has zero `ProjectReference` elements.
- `apps/backend/src/appsettings.json` or `appsettings.Development.json` retains leftover non-Serilog sections — test parses JSON and asserts `RootElement.EnumerateObject()` has only `"Serilog"`.
- `OTEL_EXPORTER_OTLP_ENDPOINT` is set when `Telemetry:OtlpEndpoint` is empty — test verifies `AddAppTelemetry` succeeds and configures OTLP exporter from `OTEL_EXPORTER_OTLP_ENDPOINT`.
- `OTEL_EXPORTER_OTLP_ENDPOINT` contains a malformed URI — test verifies `AddAppTelemetry` does not throw on invalid URI.
- `WebApplicationFactory<Program>` tests fail with `InvalidOperationException: Jwt configuration section is missing` after `appsettings.json` is stripped — tests in `ScalarApiDocsTests`, `DdosProtectionSuiteTests`, `CustomWebApplicationFactory`, and `RedisWebApplicationFactory` explicitly configure `Jwt:*`.

---

### Task 1: Create `.NET Aspire` AppHost Project in `aspire-app/` with Inline Environment Variables

**Files:**
- Create: `aspire-app/ExamPrism.AppHost.csproj`
- Create: `aspire-app/Properties/launchSettings.json`
- Create: `aspire-app/Program.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/AspireAppHostConfigurationTests.cs`

**Interfaces:**
- Consumes: `docs/superpowers/specs/2026-11-05-dotnet-aspire-setup-design.md` (Sections 2 & 3 exact file contents and env keys)
- Produces: `aspire-app/ExamPrism.AppHost.csproj`, `aspire-app/Program.cs`, `aspire-app/Properties/launchSettings.json`

- [ ] **Step 1: Write failing tests in `apps/backend/tests/ExamPrism.Tests/Infrastructure/AspireAppHostConfigurationTests.cs`**

```csharp
namespace Tests.Infrastructure;

using System.IO;
using System.Linq;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

public class AspireAppHostConfigurationTests
{
    private static string GetRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(Path.Combine(current, "apps", "backend")))
            {
                return current;
            }
            current = Directory.GetParent(current)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    [Fact]
    public void AppHostCsproj_UsesAspireAppHostSdk_AndHasNoProjectReferences()
    {
        var csprojPath = Path.Combine(GetRepoRoot(), "aspire-app", "ExamPrism.AppHost.csproj");
        File.Exists(csprojPath).Should().BeTrue("aspire-app/ExamPrism.AppHost.csproj must exist at repo root");

        var doc = XDocument.Load(csprojPath);
        doc.Root.Should().NotBeNull();
        doc.Root!.Attribute("Sdk")?.Value.Should().StartWith("Aspire.AppHost.Sdk/");
        doc.Descendants("ProjectReference").Should().BeEmpty("ExamPrism.csproj must be registered by path, not ProjectReference");
    }

    [Fact]
    public void AppHostProgram_RegistersExamPrismByRelativePath_WithPostgresRedisAndAllEnvironmentVariables()
    {
        var programPath = Path.Combine(GetRepoRoot(), "aspire-app", "Program.cs");
        File.Exists(programPath).Should().BeTrue("aspire-app/Program.cs must exist");

        var content = File.ReadAllText(programPath);
        content.Should().Contain("AddPostgres(\"postgres\")");
        content.Should().Contain("AddDatabase(\"CatalogConnection\", \"examprism_catalog_dev\")");
        content.Should().Contain("AddRedis(\"Redis\")");
        content.Should().Contain("AddProject(\"examprism-api\", \"../apps/backend/src/ExamPrism.csproj\")");

        string[] requiredEnvPrefixes =
        [
            "\"AllowedHosts\"",
            "\"Persistence__CommandTimeoutSeconds\"",
            "\"MultiTenancy__BaseDomains__0\"",
            "\"MultiTenancy__FleetMigration__MigrateOnStartup\"",
            "\"Caching__KeyPrefix\"",
            "\"Locking__KeyPrefix\"",
            "\"RateLimiting__Global__PermitLimit\"",
            "\"Jwt__SecretKey\"",
            "\"PlatformAdmin__SeedOnStartup\"",
            "\"Orleans__ClusterId\"",
            "\"Resilience__MaxRetryAttempts\"",
            "\"LoadShedding__Enabled\"",
            "\"Batching__Capacity\"",
            "\"IdentityHighLoad__MaxConcurrentPasswordHashes\"",
            "\"DdosProtection__Enabled\"",
            "\"RuntimeTuning__MaxRequestHeaderCount\"",
            "\"Telemetry__EnablePrometheusEndpoint\""
        ];

        foreach (var prefix in requiredEnvPrefixes)
        {
            content.Should().Contain(prefix);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~AspireAppHostConfigurationTests"`
Expected: FAIL with `aspire-app/ExamPrism.AppHost.csproj must exist at repo root`

- [ ] **Step 3: Create `aspire-app/ExamPrism.AppHost.csproj`, `aspire-app/Properties/launchSettings.json`, and `aspire-app/Program.cs`**

Create `aspire-app/ExamPrism.AppHost.csproj`, `aspire-app/Properties/launchSettings.json`, and `aspire-app/Program.cs` using the exact contents specified in Sections 2.2, 2.3, and 3 of `docs/superpowers/specs/2026-11-05-dotnet-aspire-setup-design.md`.

- [ ] **Step 4: Build `aspire-app/ExamPrism.AppHost.csproj` and run `AspireAppHostConfigurationTests`**

Run: `dotnet build aspire-app/ExamPrism.AppHost.csproj && dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~AspireAppHostConfigurationTests"`
Expected: Build succeeded (0 Warning(s), 0 Error(s)) and 2 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add aspire-app/ apps/backend/tests/ExamPrism.Tests/Infrastructure/AspireAppHostConfigurationTests.cs
git commit -m "feat(aspire): add root aspire-app AppHost with path-based backend registration and env vars"
```

---

### Task 2: Strip `appsettings*.json` to Serilog Only, Add Aspire OTLP Endpoint Fallback, and Update Test Hosts

**Files:**
- Modify: `apps/backend/src/Infrastructure/Telemetry/TelemetrySetup.cs:40-70`
- Modify: `apps/backend/src/appsettings.json`
- Modify: `apps/backend/src/appsettings.Development.json`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/Logging/SerilogConfigurationTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs:27-41`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/RedisIntegrationTests.cs:39-50`
- Modify: `apps/backend/tests/ExamPrism.Tests/Features/ScalarApiDocsTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs:312-327`

**Interfaces:**
- Consumes: `TelemetrySetup.AddAppTelemetry(this IServiceCollection services, IConfiguration configuration)`
- Produces: Clean `appsettings.json` and `appsettings.Development.json` (containing only `"Serilog"`), `OTEL_EXPORTER_OTLP_ENDPOINT` fallback in `TelemetrySetup.AddAppTelemetry`, and self-contained test hosts in `ExamPrism.Tests`.

- [ ] **Step 1: Add failing tests for Serilog-only `appsettings*.json` and `OTEL_EXPORTER_OTLP_ENDPOINT` fallback in `apps/backend/tests/ExamPrism.Tests/Infrastructure/Logging/SerilogConfigurationTests.cs`**

Add the following test methods to `SerilogConfigurationTests`:

```csharp
    [Fact]
    public void AppsettingsJson_And_AppsettingsDevelopmentJson_ContainOnlySerilogSection()
    {
        foreach (var fileName in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            var jsonPath = GetSrcPath(fileName);
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            keys.Should().Equal(["Serilog"], $"{fileName} should only retain the Serilog section");
        }
    }

    [Theory]
    [InlineData("http://localhost:19100")]
    [InlineData("not-a-valid-uri")]
    public void AddAppTelemetry_WhenTelemetryOtlpEndpointEmpty_FallsBackToOtelExporterOtlpEndpointEnvKey(string envEndpoint)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Telemetry:OtlpEndpoint"] = "",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = envEndpoint
            })
            .Build();

        var services = new ServiceCollection();
        var act = () => services.AddAppTelemetry(configuration);
        act.Should().NotThrow();
    }
```

- [ ] **Step 2: Run the new tests to verify failure**

Run: `dotnet test apps/backend/ExamPrism.slnx --filter "FullyQualifiedName~AppsettingsJson_And_AppsettingsDevelopmentJson_ContainOnlySerilogSection"`
Expected: FAIL because `appsettings.json` still contains `AllowedHosts`, `ConnectionStrings`, `Persistence`, etc.

- [ ] **Step 3: Update `TelemetrySetup.cs`, strip `appsettings.json` and `appsettings.Development.json`, and add `Jwt:*` settings to test hosts**

1. In `apps/backend/src/Infrastructure/Telemetry/TelemetrySetup.cs`:
   Resolve the OTLP endpoint string before `WithTracing`:
   ```csharp
   var rawOtlpEndpoint = !string.IsNullOrWhiteSpace(telemetryOptions.OtlpEndpoint)
       ? telemetryOptions.OtlpEndpoint
       : configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
   var hasOtlpEndpoint = !string.IsNullOrWhiteSpace(rawOtlpEndpoint) &&
       Uri.TryCreate(rawOtlpEndpoint, UriKind.Absolute, out var otlpEndpointUri);
   ```
   Use `if (hasOtlpEndpoint && otlpEndpointUri is not null)` in both `WithTracing` and `WithMetrics` to call `AddOtlpExporter(otlp => otlp.Endpoint = otlpEndpointUri)`.
2. Replace `apps/backend/src/appsettings.json` and `apps/backend/src/appsettings.Development.json` with the Serilog-only JSON from Sections 4.1 and 4.2 of `docs/superpowers/specs/2026-11-05-dotnet-aspire-setup-design.md`.
3. In `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs` (`ConfigureWebHost`), add:
   ```csharp
   builder.UseSetting("Jwt:Issuer", "ExamPrism");
   builder.UseSetting("Jwt:Audience", "ExamPrism");
   builder.UseSetting("Jwt:SecretKey", "SuperSecretKeyForDevJwtTokensMin256BitsLongExamPrismDevOnly!");
   builder.UseSetting("Jwt:AccessTokenExpirationMinutes", "15");
   builder.UseSetting("Jwt:RefreshTokenExpirationDays", "7");
   ```
4. In `apps/backend/tests/ExamPrism.Tests/Infrastructure/RedisIntegrationTests.cs` (`RedisWebApplicationFactory.ConfigureWebHost`), add:
   ```csharp
   builder.UseSetting("Jwt:Issuer", "ExamPrism");
   builder.UseSetting("Jwt:Audience", "ExamPrism");
   builder.UseSetting("Jwt:SecretKey", "SuperSecretKeyForDevJwtTokensMin256BitsLongExamPrismDevOnly!");
   builder.UseSetting("PlatformAdmin:SeedOnStartup", "false");
   builder.UseSetting("MultiTenancy:Provisioning:RecoverStuckProvisioningOnStartup", "false");
   builder.UseSetting("MultiTenancy:FleetMigration:MigrateOnStartup", "false");
   ```
5. In `apps/backend/tests/ExamPrism.Tests/Features/ScalarApiDocsTests.cs` and `apps/backend/tests/ExamPrism.Tests/Infrastructure/DdosProtectionSuiteTests.cs` (at line 312), configure `Jwt:Issuer` (`"ExamPrism"`), `Jwt:Audience` (`"ExamPrism"`), `Jwt:SecretKey` (`"SuperSecretKeyForDevJwtTokensMin256BitsLongExamPrismDevOnly!"`), and `MultiTenancy:Provisioning:RecoverStuckProvisioningOnStartup` (`"false"`) on each `WebApplicationFactory<Program>` builder.

- [ ] **Step 4: Run the entire test suite and AppHost build to verify everything passes**

Run: `dotnet build aspire-app/ExamPrism.AppHost.csproj && dotnet test apps/backend/ExamPrism.slnx`
Expected: `ExamPrism.AppHost` builds with 0 errors and all tests in `ExamPrism.Tests` PASS (0 failed).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/appsettings.json apps/backend/src/appsettings.Development.json apps/backend/src/Infrastructure/Telemetry/TelemetrySetup.cs apps/backend/tests/ExamPrism.Tests/
git commit -m "refactor(config): keep only Serilog in appsettings and support Aspire OTLP fallback"
```

