# EF Core PostgreSQL & Testcontainers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish EF Core PostgreSQL database persistence in `src/Infrastructure` and configure isolated integration testing using `Testcontainers.PostgreSql`.

**Architecture:** Introduce `AppDbContext` and `AddInfrastructure` extension method in `src/Infrastructure` with Central Package Management (`Directory.Packages.props`). In `tests/ExamPrism.Tests/`, build `CustomWebApplicationFactory` with `PostgreSqlContainer` to dynamically spin up and tear down a PostgreSQL instance for integration tests.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, EF Core 10, Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3, Testcontainers.PostgreSql 4.16.0, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-01-efcore-postgres-testcontainers-design.md`

## Global Constraints

- Target Framework: `net10.0` across all backend projects.
- Central Package Management enabled (`ManagePackageVersionsCentrally: true`).
- DbContext namespace: `ExamPrism.Infrastructure.Persistence`.
- DI namespace: `ExamPrism.Infrastructure`.
- PostgreSQL container image: `postgres:17-alpine`.
- Connection string configuration key: `"ConnectionStrings:DefaultConnection"`.

## Review Focus

1. Missing `ConnectionStrings:DefaultConnection` throws `InvalidOperationException` on startup.
2. `AppDbContext` is correctly registered with scoped lifetime in the DI container.
3. Database health check reports Healthy when connected, and fails if connection fails.
4. `CustomWebApplicationFactory` correctly overrides `ConnectionStrings:DefaultConnection` before building the host.
5. `CustomWebApplicationFactory` disposes container cleanly to prevent orphaned Docker containers.

---

### Task 1: Central Package Management and Project Dependencies

**Files:**
- Modify: `apps/backend/Directory.Packages.props`
- Modify: `apps/backend/src/ExamPrism.csproj`
- Modify: `apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj`

**Interfaces:**
- Produces: Package references for `Npgsql.EntityFrameworkCore.PostgreSQL`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore`, and `Testcontainers.PostgreSql`.

- [ ] **Step 1: Add package versions to `Directory.Packages.props`**

Add package versions inside `<ItemGroup>`:
```xml
<!-- EF Core & PostgreSQL Dependencies -->
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" />
<PackageVersion Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" Version="10.0.12" />

<!-- Container Test Dependencies -->
<PackageVersion Include="Testcontainers.PostgreSql" Version="4.16.0" />
```

- [ ] **Step 2: Add package references to `apps/backend/src/ExamPrism.csproj`**

Add references:
```xml
<ItemGroup>
  <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
  <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  <PackageReference Include="Microsoft.EntityFrameworkCore.Design">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
  <PackageReference Include="Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore" />
</ItemGroup>
```

- [ ] **Step 3: Add package reference to `apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj`**

Add `Testcontainers.PostgreSql` reference:
```xml
<ItemGroup>
  <PackageReference Include="Testcontainers.PostgreSql" />
</ItemGroup>
```

- [ ] **Step 4: Verify package restore succeeds**

Run: `dotnet restore apps/backend/ExamPrism.slnx`
Expected: `Restore completed in ...` with 0 errors.

- [ ] **Step 5: Commit package configurations**

```bash
git add apps/backend/Directory.Packages.props apps/backend/src/ExamPrism.csproj apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj
git commit -m "chore(deps): add EF Core PostgreSQL and Testcontainers packages"
```

---

### Task 2: Implement `AppDbContext`, Infrastructure DI, and AppSettings Configuration

**Files:**
- Create: `apps/backend/src/Infrastructure/Persistence/AppDbContext.cs`
- Create: `apps/backend/src/Infrastructure/DependencyInjection.cs`
- Modify: `apps/backend/src/appsettings.json`
- Modify: `apps/backend/src/appsettings.Development.json`
- Modify: `apps/backend/src/Program.cs`
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`

**Interfaces:**
- Consumes: `IConfiguration`, `IServiceCollection`
- Produces:
  - `class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)`
  - `DependencyInjection.AddInfrastructure(this IServiceCollection services, IConfiguration configuration) -> IServiceCollection`

- [ ] **Step 1: Write failing test for missing connection string and DI registration**

File: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`
```csharp
namespace Tests.Infrastructure;

using ExamPrism.Infrastructure;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class DependencyInjectionTests
{
    [Fact]
    public void AddInfrastructure_ThrowsInvalidOperationException_WhenConnectionStringMissing()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddInfrastructure(configuration));

        Assert.Contains("Connection string 'DefaultConnection' not found", exception.Message);
    }

    [Fact]
    public void AddInfrastructure_RegistersAppDbContext_WhenConnectionStringProvided()
    {
        var services = new ServiceCollection();
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=test;Username=postgres;Password=postgres"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        services.AddInfrastructure(configuration);

        var serviceDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(AppDbContext));
        Assert.NotNull(serviceDescriptor);
        Assert.Equal(ServiceLifetime.Scoped, serviceDescriptor.Lifetime);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ --filter "FullyQualifiedName~DependencyInjectionTests"`
Expected: FAIL with compilation error (`ExamPrism.Infrastructure` not found).

- [ ] **Step 3: Implement `AppDbContext.cs`**

File: `apps/backend/src/Infrastructure/Persistence/AppDbContext.cs`
```csharp
namespace ExamPrism.Infrastructure.Persistence;

using Microsoft.EntityFrameworkCore;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
}
```

- [ ] **Step 4: Implement `DependencyInjection.cs`**

File: `apps/backend/src/Infrastructure/DependencyInjection.cs`
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

- [ ] **Step 5: Configure `appsettings.json` and `appsettings.Development.json`**

Add `"ConnectionStrings"` block:
```json
"ConnectionStrings": {
  "DefaultConnection": "Host=localhost;Port=5432;Database=examprism_dev;Username=postgres;Password=postgres"
}
```

- [ ] **Step 6: Update `apps/backend/src/Program.cs`**

Wire `AddInfrastructure`:
```csharp
using Common.Endpoints;
using ExamPrism.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapEndpoints();

app.Run();

public partial class Program { }
```

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ --filter "FullyQualifiedName~DependencyInjectionTests"`
Expected: PASS (2 passed).

- [ ] **Step 8: Commit infrastructure DI and DbContext**

```bash
git add apps/backend/src/Infrastructure apps/backend/src/appsettings.json apps/backend/src/appsettings.Development.json apps/backend/src/Program.cs apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs
git commit -m "feat(infrastructure): add AppDbContext and AddInfrastructure DI registration"
```

---

### Task 3: Implement `CustomWebApplicationFactory` with Testcontainers

**Files:**
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs`

**Interfaces:**
- Produces: `class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime`
  - `InitializeAsync() -> Task`
  - `DisposeAsync() -> Task`
  - `ConfigureWebHost(IWebHostBuilder builder) -> void`

- [ ] **Step 1: Implement `CustomWebApplicationFactory.cs`**

File: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs`
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

- [ ] **Step 2: Build tests project to verify compilation**

Run: `dotnet build apps/backend/tests/ExamPrism.Tests/`
Expected: `Build succeeded` with 0 errors.

- [ ] **Step 3: Commit `CustomWebApplicationFactory`**

```bash
git add apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs
git commit -m "test(infrastructure): implement CustomWebApplicationFactory with Testcontainers"
```

---

### Task 4: Add Database Integration Tests & Update HealthEndpointTests

**Files:**
- Create: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DatabaseIntegrationTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/HealthEndpointTests.cs`

**Interfaces:**
- Consumes: `CustomWebApplicationFactory`, `AppDbContext`
- Produces: Verified integration tests against real PostgreSQL container for DB connectivity and `/health` checks.

- [ ] **Step 1: Create `DatabaseIntegrationTests.cs`**

File: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DatabaseIntegrationTests.cs`
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

- [ ] **Step 2: Update `HealthEndpointTests.cs` to use `CustomWebApplicationFactory`**

File: `apps/backend/tests/ExamPrism.Tests/HealthEndpointTests.cs`
Change base fixture from `WebApplicationFactory<Program>` to `CustomWebApplicationFactory`:
```csharp
namespace Tests;

using System.Net;
using System.Net.Http.Json;
using Endpoints;
using Infrastructure;
using Xunit;

public class HealthEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/Health")]
    [InlineData("/HEALTH")]
    public async Task GetHealth_HandlesCaseVariations(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(content);
        Assert.Equal("Healthy", content.Status);
    }

    [Fact]
    public async Task GetOpenApi_ReturnsOk_InDevelopment()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("openapi", json, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 3: Run all backend tests**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/`
Expected: All tests pass.

- [ ] **Step 4: Commit database integration tests and health tests migration**

```bash
git add apps/backend/tests/ExamPrism.Tests/Infrastructure/DatabaseIntegrationTests.cs apps/backend/tests/ExamPrism.Tests/HealthEndpointTests.cs
git commit -m "test(api): add database integration tests and update HealthEndpointTests"
```
