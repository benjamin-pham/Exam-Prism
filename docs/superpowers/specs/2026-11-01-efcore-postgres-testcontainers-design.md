# Design Spec: EF Core PostgreSQL & Testcontainers Setup

- **Date:** 2026-11-01
- **Status:** Proposed
- **Target Path:** `apps/backend/`

## 1. Overview & Goals

Establish a robust relational persistence foundation using Entity Framework Core with PostgreSQL for Exam-Prism, coupled with isolated, reproducible integration testing powered by Testcontainers.

### Key Goals:
- Configure Central Package Management (`Directory.Packages.props`) for EF Core PostgreSQL, EF Core Design, EF Core Health Checks, and Testcontainers.
- Introduce an initial `AppDbContext` located under `src/Infrastructure/Persistence/`.
- Provide an extension method `AddInfrastructure` in `src/Infrastructure/DependencyInjection.cs` to configure Npgsql DbContext and integrate DB health checks with ASP.NET Core Health Checks.
- Configure default database connection strings in `appsettings.json` and `appsettings.Development.json`.
- Implement a reusable integration test harness in `tests/ExamPrism.Tests/` using `Testcontainers.PostgreSql` (`CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime`) that manages the container lifecycle and overrides the connection string at runtime.
- Add integration tests verifying database connectivity via `DbContext.Database.CanConnectAsync()` and verify that the `/health` endpoint responds with `200 OK` and `"status": "Healthy"`.

---

## 2. Architecture & Directory Layout

```text
apps/backend/
├── Directory.Packages.props
├── src/
│   ├── ExamPrism.csproj
│   ├── Program.cs
│   ├── appsettings.json
│   ├── appsettings.Development.json
│   └── Infrastructure/
│       ├── DependencyInjection.cs
│       └── Persistence/
│           └── AppDbContext.cs
└── tests/
    └── ExamPrism.Tests/
        ├── ExamPrism.Tests.csproj
        ├── HealthEndpointTests.cs
        └── Infrastructure/
            ├── CustomWebApplicationFactory.cs
            └── DatabaseIntegrationTests.cs
```

---

## 3. Package Management & Dependencies

### 3.1 Central Package Management (`Directory.Packages.props`)
Add the following package versions:
- `Npgsql.EntityFrameworkCore.PostgreSQL` (`10.0.3`): PostgreSQL EF Core provider.
- `Microsoft.EntityFrameworkCore.Design` (`10.0.12`): EF Core tooling and migration runtime.
- `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore` (`10.0.12`): Health check integration for EF Core DbContext.
- `Testcontainers.PostgreSql` (`4.16.0`): PostgreSQL container module for .NET Testcontainers.

### 3.2 Project Dependencies
- **`apps/backend/src/ExamPrism.csproj`**:
  - `Npgsql.EntityFrameworkCore.PostgreSQL`
  - `Microsoft.EntityFrameworkCore.Design` (`PrivateAssets="all"`)
  - `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`
- **`apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj`**:
  - `Testcontainers.PostgreSql`

---

## 4. Implementation Details

### 4.1 `AppDbContext`
File: `apps/backend/src/Infrastructure/Persistence/AppDbContext.cs`
- Namespace: `ExamPrism.Infrastructure.Persistence`
- Inherits from `Microsoft.EntityFrameworkCore.DbContext`.
- Uses primary constructor:
  ```csharp
  namespace ExamPrism.Infrastructure.Persistence;

  using Microsoft.EntityFrameworkCore;

  public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
  {
  }
  ```

### 4.2 Dependency Injection Extension
File: `apps/backend/src/Infrastructure/DependencyInjection.cs`
- Namespace: `ExamPrism.Infrastructure`
- Method: `public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)`
- Resolves `"ConnectionStrings:DefaultConnection"`; throws `InvalidOperationException` if missing or empty.
- Registers `AppDbContext` with `options.UseNpgsql(connectionString)`.
- Registers EF Core health check using `services.AddHealthChecks().AddDbContextCheck<AppDbContext>(name: "database")`.

```csharp
namespace ExamPrism.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>(name: "database");

        return services;
    }
}
```

### 4.3 AppSettings Configuration
Files: `apps/backend/src/appsettings.json` and `apps/backend/src/appsettings.Development.json`
- Add connection string block:
  ```json
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=examprism_dev;Username=postgres;Password=postgres"
  }
  ```

### 4.4 `Program.cs` Integration
File: `apps/backend/src/Program.cs`
- Call `builder.Services.AddInfrastructure(builder.Configuration);`.
- `HealthEndpoints.cs` maps `/health`, which checks registered health checks including `"database"`.

---

## 5. Testing Infrastructure

### 5.1 `CustomWebApplicationFactory`
File: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs`
- Inherits `WebApplicationFactory<Program>` and implements `xunit.IAsyncLifetime`.
- Configures `PostgreSqlContainer` using image `postgres:17-alpine`:
  ```csharp
  namespace Tests.Infrastructure;

  using Microsoft.AspNetCore.Hosting;
  using Microsoft.AspNetCore.Mvc.Testing;
  using Testcontainers.PostgreSql;
  using Xunit;

  public class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
  {
      private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:17-alpine")
          .WithDatabase("examprism_test")
          .WithUsername("postgres")
          .WithPassword("postgres")
          .Build();

      protected override void ConfigureWebHost(IWebHostBuilder builder)
      {
          builder.UseSetting("ConnectionStrings:DefaultConnection", _dbContainer.GetConnectionString());
      }

      public async Task InitializeAsync()
      {
          await _dbContainer.StartAsync();
      }

      public new async Task DisposeAsync()
      {
          await _dbContainer.StopAsync();
          await _dbContainer.DisposeAsync();
      }
  }
  ```

### 5.2 Database Integration Tests
File: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DatabaseIntegrationTests.cs`
- Verifies:
  1. `AppDbContext.Database.CanConnectAsync()` returns `true`.
  2. `GET /health` returns HTTP 200 with status `"Healthy"`.

```csharp
namespace Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using Endpoints;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class DatabaseIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DatabaseIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CanConnectToDatabase_ReturnsTrue()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var canConnect = await dbContext.Database.CanConnectAsync();

        Assert.True(canConnect);
    }

    [Fact]
    public async Task HealthEndpoint_WithDatabaseCheck_ReturnsHealthy()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(content);
        Assert.Equal("Healthy", content.Status);
    }
}
```

### 5.3 Existing `HealthEndpointTests`
- Update `HealthEndpointTests` to use `IClassFixture<CustomWebApplicationFactory>` so that testing `/health` and `/openapi/v1.json` runs in an environment with a valid running PostgreSQL container.

---

## 6. Verification & Self-Review

### Spec Self-Review Checklist:
- [x] **Placeholder scan:** No TBD, TODO, or undefined configurations.
- [x] **Internal consistency:** Package versions match between `Directory.Packages.props` and projects; namespaces match directory structure (`ExamPrism.Infrastructure.Persistence`).
- [x] **Scope check:** Strictly covers EF Core PostgreSQL setup + Testcontainers infrastructure without prematurely adding domain entities or migrations.
- [x] **Ambiguity check:** File paths, classes, and method signatures are fully specified.
