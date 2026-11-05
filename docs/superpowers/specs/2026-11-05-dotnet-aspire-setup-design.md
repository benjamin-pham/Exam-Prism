# .NET Aspire AppHost Setup & Configuration Migration Design

**Date:** 2026-11-05
**Status:** Approved

## 1. Overview & Goals

Set up a `.NET Aspire` orchestrator (`aspire-app`) at the repository root to manage the local development stack for `ExamPrism` without coupling the AppHost project to backend source code via MSBuild `<ProjectReference>`. Migrate all application and infrastructure settings (except `Serilog`) out of `apps/backend/src/appsettings.json` and `apps/backend/src/appsettings.Development.json` into environment variables declared directly in `aspire-app/Program.cs`.

### Key Requirements
1. **Root AppHost Project:** Create `aspire-app/ExamPrism.AppHost.csproj` at the repository root (`/aspire-app`).
2. **Path-Based Project Registration:** Register `apps/backend/src/ExamPrism.csproj` using `builder.AddProject("examprism-api", "../apps/backend/src/ExamPrism.csproj")` without adding a `<ProjectReference>` in `ExamPrism.AppHost.csproj`.
3. **Full Local Infrastructure Stack:** Orchestrate PostgreSQL (`CatalogConnection` pointing to database `examprism_catalog_dev`) and Redis (`Redis`) container resources in Aspire and wire them into `examprism-api` with `.WithReference(...)` and `.WaitFor(...)`.
4. **Inline Environment Configuration in `aspire-app/Program.cs`:** Move all non-Serilog configuration values from `appsettings.json` and `appsettings.Development.json` directly into `aspire-app/Program.cs` via `.WithEnvironment(...)` calls.
5. **Clean `appsettings*.json`:** Strip all sections except `Serilog` from `apps/backend/src/appsettings.json` and `apps/backend/src/appsettings.Development.json`.
6. **Aspire Dashboard OTLP Support:** Update `TelemetrySetup.cs` to fall back to `OTEL_EXPORTER_OTLP_ENDPOINT` when `Telemetry:OtlpEndpoint` is empty so traces and metrics export automatically to the Aspire Dashboard.
7. **Test Suite Independence:** Update test hosts (`CustomWebApplicationFactory` and tests using `WebApplicationFactory<Program>`) to supply required startup settings (`Jwt:*`, etc.) explicitly so all backend tests pass with the stripped `appsettings*.json` files.

---

## 2. Project Structure & AppHost Configuration

### 2.1 Directory Layout

```text
Exam-Prism/
├── aspire-app/
│   ├── Properties/
│   │   └── launchSettings.json
│   ├── ExamPrism.AppHost.csproj
│   └── Program.cs
└── apps/
    └── backend/
        ├── src/
        │   ├── ExamPrism.csproj
        │   ├── appsettings.json              (Serilog only)
        │   └── appsettings.Development.json  (Serilog only)
        └── tests/
            └── ExamPrism.Tests/
```

### 2.2 `aspire-app/ExamPrism.AppHost.csproj`

Uses `Aspire.AppHost.Sdk/13.6.1` targeting `net10.0` with no `<ProjectReference>` to `ExamPrism.csproj`:

```xml
<Project Sdk="Aspire.AppHost.Sdk/13.6.1">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UserSecretsId>examprism-apphost-dev</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.AppHost" Version="13.6.1" />
    <PackageReference Include="Aspire.Hosting.PostgreSQL" Version="13.6.1" />
    <PackageReference Include="Aspire.Hosting.Redis" Version="13.6.1" />
  </ItemGroup>

</Project>
```

### 2.3 `aspire-app/Properties/launchSettings.json`

Provides standard local Aspire Dashboard endpoints for development:

```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "http": {
      "commandName": "Project",
      "dotnetRunMessages": true,
      "launchBrowser": true,
      "applicationUrl": "http://localhost:15100",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development",
        "DOTNET_ENVIRONMENT": "Development",
        "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL": "http://localhost:19100",
        "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL": "http://localhost:20100",
        "ASPIRE_ALLOW_UNSECURED_TRANSPORT": "true"
      }
    }
  }
}
```

---

## 3. Resource Topology & Environment Variables in `aspire-app/Program.cs`

`aspire-app/Program.cs` provisions the PostgreSQL server (`postgres`), database (`CatalogConnection` with database name `examprism_catalog_dev`), Redis (`Redis`), and registers `examprism-api` by relative file path `"../apps/backend/src/ExamPrism.csproj"`.

All configuration keys previously in `appsettings.json` and `appsettings.Development.json` (excluding `Serilog`, which remains in `appsettings*.json`, and `ConnectionStrings`, which Aspire injects via `.WithReference(catalogDb)` and `.WithReference(redis)`) are configured inline on the `examprism-api` resource builder:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var catalogDb = postgres.AddDatabase("CatalogConnection", "examprism_catalog_dev");
var redis = builder.AddRedis("Redis");

builder.AddProject("examprism-api", "../apps/backend/src/ExamPrism.csproj")
    .WithReference(catalogDb)
    .WithReference(redis)
    .WaitFor(catalogDb)
    .WaitFor(redis)
    .WithEnvironment("AllowedHosts", "*")
    // Persistence
    .WithEnvironment("Persistence__CommandTimeoutSeconds", "30")
    .WithEnvironment("Persistence__MaxBatchSize", "100")
    .WithEnvironment("Persistence__MaxRetryCount", "3")
    .WithEnvironment("Persistence__MaxRetryDelaySeconds", "5")
    .WithEnvironment("Persistence__SlowQueryThresholdMs", "500")
    .WithEnvironment("Persistence__CatalogReadConnection", "")
    .WithEnvironment("Persistence__TenantReadReplicaHost", "")
    // MultiTenancy
    .WithEnvironment("MultiTenancy__BaseDomains__0", "examprism.com")
    .WithEnvironment("MultiTenancy__BaseDomains__1", "localhost")
    .WithEnvironment("MultiTenancy__ReservedSubdomains__0", "www")
    .WithEnvironment("MultiTenancy__ReservedSubdomains__1", "admin")
    .WithEnvironment("MultiTenancy__ReservedSubdomains__2", "api")
    .WithEnvironment("MultiTenancy__AllowHeaderFallbackInDevelopment", "true")
    .WithEnvironment("MultiTenancy__HeaderName", "X-Tenant")
    .WithEnvironment("MultiTenancy__CatalogCacheExpirationMinutes", "30")
    .WithEnvironment("MultiTenancy__CatalogLocalCacheExpirationMinutes", "5")
    .WithEnvironment("MultiTenancy__NotFoundCacheExpirationSeconds", "30")
    .WithEnvironment("MultiTenancy__MaxNegativeCacheEntries", "2000")
    .WithEnvironment("MultiTenancy__MaxCatalogDbMissesPerSecond", "50")
    .WithEnvironment("MultiTenancy__ProvisioningRetryAfterSeconds", "5")
    .WithEnvironment("MultiTenancy__DatabasePool__MaxPoolSize", "15")
    .WithEnvironment("MultiTenancy__DatabasePool__MinPoolSize", "0")
    .WithEnvironment("MultiTenancy__DatabasePool__ConnectionIdleLifetimeSeconds", "30")
    .WithEnvironment("MultiTenancy__DatabasePool__ConnectionPruningIntervalSeconds", "10")
    .WithEnvironment("MultiTenancy__DatabasePool__NoResetOnClose", "true")
    .WithEnvironment("MultiTenancy__DatabasePool__KeepAliveSeconds", "30")
    .WithEnvironment("MultiTenancy__Provisioning__ServerConnectionString", "")
    .WithEnvironment("MultiTenancy__Provisioning__DatabaseNamePrefix", "examprism_tenant_")
    .WithEnvironment("MultiTenancy__Provisioning__DedicatedUserPrefix", "examprism_u_")
    .WithEnvironment("MultiTenancy__Provisioning__CreateDedicatedPostgresUser", "false")
    .WithEnvironment("MultiTenancy__Provisioning__RecoverStuckProvisioningOnStartup", "true")
    .WithEnvironment("MultiTenancy__FleetMigration__MigrateOnStartup", "true")
    .WithEnvironment("MultiTenancy__FleetMigration__MaxDegreeOfParallelism", "10")
    // Caching
    .WithEnvironment("Caching__KeyPrefix", "examprism:")
    .WithEnvironment("Caching__DefaultExpirationMinutes", "30")
    .WithEnvironment("Caching__DefaultLocalExpirationMinutes", "5")
    .WithEnvironment("Caching__EnableBackplane", "true")
    .WithEnvironment("Caching__BackplaneChannelName", "cache:backplane")
    .WithEnvironment("Caching__OutputCacheExpirationSeconds", "30")
    // Locking
    .WithEnvironment("Locking__KeyPrefix", "lock:")
    .WithEnvironment("Locking__DefaultExpirySeconds", "30")
    // RateLimiting
    .WithEnvironment("RateLimiting__Global__PermitLimit", "100")
    .WithEnvironment("RateLimiting__Global__WindowSeconds", "60")
    .WithEnvironment("RateLimiting__Auth__PermitLimit", "10")
    .WithEnvironment("RateLimiting__Auth__WindowSeconds", "60")
    .WithEnvironment("RateLimiting__TenantOnboarding__PermitLimit", "5")
    .WithEnvironment("RateLimiting__TenantOnboarding__WindowSeconds", "60")
    .WithEnvironment("RateLimiting__TenantOnboarding__SegmentsPerWindow", "6")
    // Jwt
    .WithEnvironment("Jwt__Issuer", "ExamPrism")
    .WithEnvironment("Jwt__Audience", "ExamPrism")
    .WithEnvironment("Jwt__SecretKey", "SuperSecretKeyForDevJwtTokensMin256BitsLongExamPrismDevOnly!")
    .WithEnvironment("Jwt__AccessTokenExpirationMinutes", "15")
    .WithEnvironment("Jwt__RefreshTokenExpirationDays", "7")
    // PlatformAdmin
    .WithEnvironment("PlatformAdmin__SeedOnStartup", "true")
    .WithEnvironment("PlatformAdmin__Email", "admin@examprism.com")
    .WithEnvironment("PlatformAdmin__Password", "AdminPassword123!")
    // Orleans
    .WithEnvironment("Orleans__ClusterId", "examprism-cluster")
    .WithEnvironment("Orleans__ServiceId", "examprism-service")
    .WithEnvironment("Orleans__StateStoreName", "Default")
    .WithEnvironment("Orleans__SiloPort", "11111")
    .WithEnvironment("Orleans__GatewayPort", "30000")
    .WithEnvironment("Orleans__AdvertisedIpAddress", "")
    .WithEnvironment("Orleans__LoadSheddingEnabled", "true")
    .WithEnvironment("Orleans__LoadSheddingLimitPercent", "95")
    .WithEnvironment("Orleans__CollectionAgeMinutes", "15")
    .WithEnvironment("Orleans__CollectionQuantumMinutes", "1")
    .WithEnvironment("Orleans__ResponseTimeoutSeconds", "15")
    // Resilience
    .WithEnvironment("Resilience__MaxRetryAttempts", "3")
    .WithEnvironment("Resilience__BackoffBaseDelaySeconds", "0.5")
    .WithEnvironment("Resilience__CircuitBreakerFailureRatio", "0.5")
    .WithEnvironment("Resilience__CircuitBreakerSamplingDurationSeconds", "30")
    .WithEnvironment("Resilience__CircuitBreakerBreakDurationSeconds", "15")
    .WithEnvironment("Resilience__CircuitBreakerMinimumThroughput", "20")
    .WithEnvironment("Resilience__TimeoutSeconds", "10")
    // LoadShedding
    .WithEnvironment("LoadShedding__Enabled", "true")
    .WithEnvironment("LoadShedding__InitialConcurrencyLimit", "100")
    .WithEnvironment("LoadShedding__MinConcurrencyLimit", "20")
    .WithEnvironment("LoadShedding__MaxConcurrencyLimit", "500")
    .WithEnvironment("LoadShedding__RetryAfterSeconds", "2")
    // Batching
    .WithEnvironment("Batching__Capacity", "10000")
    .WithEnvironment("Batching__MaxBatchSize", "100")
    .WithEnvironment("Batching__FlushIntervalMs", "100")
    // IdentityHighLoad
    .WithEnvironment("IdentityHighLoad__MaxConcurrentPasswordHashes", "8")
    .WithEnvironment("IdentityHighLoad__PasswordHashQueueTimeoutMs", "2000")
    .WithEnvironment("IdentityHighLoad__EnableTokenRevocationCheck", "true")
    .WithEnvironment("IdentityHighLoad__TokenRevocationCacheTtlMinutes", "1440")
    // DdosProtection
    .WithEnvironment("DdosProtection__Enabled", "true")
    .WithEnvironment("DdosProtection__BanThresholdScore", "100")
    .WithEnvironment("DdosProtection__StrikeWindowSeconds", "300")
    .WithEnvironment("DdosProtection__BanDurationSeconds", "900")
    .WithEnvironment("DdosProtection__UnknownTenantProbeWeight", "25")
    .WithEnvironment("DdosProtection__RateLimitExceededWeight", "15")
    .WithEnvironment("DdosProtection__MalformedPayloadWeight", "20")
    .WithEnvironment("DdosProtection__DecompressionBombWeight", "50")
    .WithEnvironment("DdosProtection__BypassPenaltyBoxForValidJwt", "true")
    .WithEnvironment("DdosProtection__MaxDecompressedBodySizeBytes", "10485760")
    .WithEnvironment("DdosProtection__MaxDecompressionRatio", "20")
    // RuntimeTuning
    .WithEnvironment("RuntimeTuning__MinRequestBodyDataRateBytesPerSecond", "240")
    .WithEnvironment("RuntimeTuning__MinResponseDataRateBytesPerSecond", "240")
    .WithEnvironment("RuntimeTuning__DataRateGracePeriodSeconds", "5")
    .WithEnvironment("RuntimeTuning__MaxRequestHeaderCount", "100")
    .WithEnvironment("RuntimeTuning__MaxRequestHeadersTotalSizeBytes", "32768")
    .WithEnvironment("RuntimeTuning__MaxRequestLineSizeBytes", "8192")
    // Telemetry
    .WithEnvironment("Telemetry__OtlpEndpoint", "")
    .WithEnvironment("Telemetry__EnablePrometheusEndpoint", "true")
    .WithEnvironment("Telemetry__TraceSamplingRatio", "1.0")
    .WithEnvironment("Telemetry__AlwaysSampleSlowRequestsMs", "500");

builder.Build().Run();
```

---

## 4. Cleaned `appsettings.json` and `appsettings.Development.json`

### 4.1 `apps/backend/src/appsettings.json`

Retains only the `Serilog` section:

```json
{
  "Serilog": {
    "Using": [ "Serilog.Sinks.Console", "Serilog.Formatting.Compact" ],
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft.AspNetCore": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "formatter": "Serilog.Formatting.Compact.RenderedCompactJsonFormatter, Serilog.Formatting.Compact"
        }
      }
    ],
    "Enrich": [ "FromLogContext" ]
  }
}
```

### 4.2 `apps/backend/src/appsettings.Development.json`

Retains only the `Serilog` section:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft.AspNetCore": "Information"
      }
    },
    "WriteTo": [
      {
        "Name": "Console",
        "Args": {
          "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}"
        }
      }
    ]
  }
}
```

---

## 5. Telemetry & Test Adjustments

### 5.1 OTLP Fallback in `TelemetrySetup.cs`

In `apps/backend/src/Infrastructure/Telemetry/TelemetrySetup.cs`, resolve the OTLP endpoint from `telemetryOptions.OtlpEndpoint` first, and if empty or whitespace, fall back to `configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]`. When a valid absolute URI is present, register `.AddOtlpExporter(otlp => otlp.Endpoint = endpointUri)` on both tracing and metrics builders.

### 5.2 Backend Test Suite Independence

Because `IdentitySetup.AddIdentity` requires `Jwt:SecretKey` (at least 32 bytes), `Jwt:Issuer`, and `Jwt:Audience` at host build time, any test using `WebApplicationFactory<Program>` that previously relied on `appsettings.json` providing `Jwt:*` must configure those settings explicitly:

- `CustomWebApplicationFactory`: Add default `Jwt:Issuer`, `Jwt:Audience`, `Jwt:SecretKey`, `Jwt:AccessTokenExpirationMinutes`, `Jwt:RefreshTokenExpirationDays`, and `MultiTenancy:BaseDomains:1` (`examprism.com`) in `ConfigureWebHost`.
- `RedisWebApplicationFactory` (`RedisIntegrationTests.cs`): Ensure `Jwt:*` settings are provided if not inheriting from `CustomWebApplicationFactory`.
- Direct `new WebApplicationFactory<Program>()` usages in `ScalarApiDocsTests.cs`, `DdosProtectionSuiteTests.cs`, and any other test files: Provide `Jwt:*` settings in `WithWebHostBuilder`.

### 5.3 Verification Plan

1. Run `dotnet build aspire-app/ExamPrism.AppHost.csproj` and verify clean compilation with zero `<ProjectReference>` to `ExamPrism.csproj`.
2. Verify `apps/backend/src/appsettings.json` and `apps/backend/src/appsettings.Development.json` contain only `Serilog`.
3. Run `dotnet test apps/backend/ExamPrism.slnx` to verify all unit, architecture, and integration tests pass.

