# JWT Authentication with Refresh Token Rotation in HttpOnly Cookies Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement a complete JWT authentication subsystem with ASP.NET Core Identity, sliding refresh token rotation, reuse detection, and HttpOnly cookies.

**Architecture:** ASP.NET Core Identity on PostgreSQL EF Core for user management, dedicated `RefreshToken` entity with SHA-256 hash storage, `ITokenService` for JWT generation and CSPRNG tokens, `JwtBearerEvents` extracting tokens from HttpOnly cookies with bearer fallback, and Minimal API endpoints via `IEndpoint`.

**Tech Stack:** .NET 10, ASP.NET Core Identity, Entity Framework Core, PostgreSQL (`Npgsql.EntityFrameworkCore.PostgreSQL`), `Microsoft.AspNetCore.Authentication.JwtBearer`, xUnit, FluentAssertions, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-01-jwt-refresh-tokens-design.md`

## Global Constraints

* Target framework: `net10.0`.
* Manage package versions centrally in `Directory.Packages.props`.
* Use `Guid` as primary key for `ApplicationUser`, `IdentityRole`, and `RefreshToken`.
* Refresh tokens in DB must store deterministic SHA-256 hashes (`TokenHash`), never raw tokens.
* Tokens delivered via cookies named `access_token` and `refresh_token` with `HttpOnly = true`, `SameSite = SameSiteMode.Lax`, `Path = "/"`.
* Time logic must use `TimeProvider` (registered singleton in DI, mockable with `FakeTimeProvider`).

## Review Focus

1. **Reused revoked refresh token**: When a previously rotated/revoked token hash is presented, all active tokens for that user must be marked revoked (`RevokedAt = now`) and return 401. (Exercised in Task 6).
2. **Expired access token rejection**: Request to protected `/api/auth/me` with expired access token cookie returns 401 Unauthorized without throwing unhandled exceptions. (Exercised in Task 6).
3. **Bearer header fallback**: Request with valid `Authorization: Bearer <token>` header succeeds even if `access_token` cookie is missing. (Exercised in Task 6).
4. **Duplicate registration email**: Registering with an already existing email returns 400 Bad Request with ValidationProblem details. (Exercised in Task 6).
5. **Secure logout**: Calling logout with a refresh token revokes that token in database and deletes both cookies on response headers. (Exercised in Task 6).

---

### Task 1: Package Dependencies & Data Models (`ApplicationUser`, `RefreshToken`, `AppDbContext`)

**Files:**
- Modify: `apps/backend/Directory.Packages.props`
- Modify: `apps/backend/src/ExamPrism.csproj`
- Create: `apps/backend/src/Infrastructure/Identity/ApplicationUser.cs`
- Create: `apps/backend/src/Infrastructure/Identity/RefreshToken.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/AppDbContext.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Identity/RefreshTokenTests.cs`

**Interfaces:**
- Produces:
  - `ApplicationUser : IdentityUser<Guid>` with property `ICollection<RefreshToken> RefreshTokens`.
  - `RefreshToken` entity with `Guid Id`, `Guid UserId`, `ApplicationUser User`, `string TokenHash`, `DateTimeOffset ExpiresAt`, `DateTimeOffset CreatedAt`, `DateTimeOffset? RevokedAt`, `string? ReplacedByTokenHash`, `bool IsExpired(TimeProvider)`, `bool IsRevoked`, `bool IsActive(TimeProvider)`.
  - `AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>` with `DbSet<RefreshToken> RefreshTokens`.

- [ ] **Step 1: Write failing unit test for `RefreshToken` lifecycle properties**

Create `apps/backend/tests/ExamPrism.Tests/Identity/RefreshTokenTests.cs`:
```csharp
namespace ExamPrism.Tests.Identity;

using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class RefreshTokenTests
{
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void IsActive_WhenNotRevokedAndNotExpired_ReturnsTrue()
    {
        var token = new RefreshToken
        {
            ExpiresAt = _timeProvider.GetUtcNow().AddDays(7),
            CreatedAt = _timeProvider.GetUtcNow(),
            RevokedAt = null
        };

        token.IsActive(_timeProvider).Should().BeTrue();
        token.IsRevoked.Should().BeFalse();
        token.IsExpired(_timeProvider).Should().BeFalse();
    }

    [Fact]
    public void IsActive_WhenExpired_ReturnsFalse()
    {
        var token = new RefreshToken
        {
            ExpiresAt = _timeProvider.GetUtcNow().AddDays(-1),
            CreatedAt = _timeProvider.GetUtcNow().AddDays(-8),
            RevokedAt = null
        };

        token.IsActive(_timeProvider).Should().BeFalse();
        token.IsExpired(_timeProvider).Should().BeTrue();
    }

    [Fact]
    public void IsActive_WhenRevoked_ReturnsFalse()
    {
        var token = new RefreshToken
        {
            ExpiresAt = _timeProvider.GetUtcNow().AddDays(7),
            CreatedAt = _timeProvider.GetUtcNow(),
            RevokedAt = _timeProvider.GetUtcNow()
        };

        token.IsActive(_timeProvider).Should().BeFalse();
        token.IsRevoked.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails compilation**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter FullyQualifiedName~RefreshTokenTests`  
Expected: Compilation failure (types do not exist yet).

- [ ] **Step 3: Add package dependencies in `Directory.Packages.props` and `ExamPrism.csproj`**

In `apps/backend/Directory.Packages.props`:
Add:
```xml
<PackageVersion Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.14" />
<PackageVersion Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" Version="10.0.12" />
<PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.0.0" />
```
In `apps/backend/src/ExamPrism.csproj`:
Add:
```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" />
<PackageReference Include="Microsoft.AspNetCore.Identity.EntityFrameworkCore" />
```
In `apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj`:
Add:
```xml
<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" />
```

- [ ] **Step 4: Implement `ApplicationUser`, `RefreshToken`, and update `AppDbContext`**

Create `apps/backend/src/Infrastructure/Identity/ApplicationUser.cs`:
```csharp
namespace ExamPrism.Infrastructure.Identity;

using Microsoft.AspNetCore.Identity;

public class ApplicationUser : IdentityUser<Guid>
{
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
```

Create `apps/backend/src/Infrastructure/Identity/RefreshToken.cs`:
```csharp
namespace ExamPrism.Infrastructure.Identity;

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }

    public bool IsExpired(TimeProvider timeProvider) => timeProvider.GetUtcNow() >= ExpiresAt;
    public bool IsRevoked => RevokedAt != null;
    public bool IsActive(TimeProvider timeProvider) => !IsRevoked && !IsExpired(timeProvider);
}
```

Modify `apps/backend/src/Infrastructure/Persistence/AppDbContext.cs`:
```csharp
namespace ExamPrism.Infrastructure.Persistence;

using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<RefreshToken>(b =>
        {
            b.HasKey(rt => rt.Id);
            b.Property(rt => rt.TokenHash).IsRequired().HasMaxLength(128);
            b.HasIndex(rt => rt.TokenHash).IsUnique();
            b.HasIndex(rt => rt.UserId);

            b.HasOne(rt => rt.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
```

- [ ] **Step 5: Run unit tests to verify they pass**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter FullyQualifiedName~RefreshTokenTests`  
Expected: PASS (3 tests passed).

- [ ] **Step 6: Commit**

```bash
git add apps/backend/Directory.Packages.props apps/backend/src/ExamPrism.csproj apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj apps/backend/src/Infrastructure/Identity/ apps/backend/src/Infrastructure/Persistence/AppDbContext.cs apps/backend/tests/ExamPrism.Tests/Identity/RefreshTokenTests.cs
git commit -m "feat(identity): add ApplicationUser, RefreshToken, and IdentityDbContext configuration"
```

---

### Task 2: Token Service & JWT Options (`JwtOptions`, `ITokenService`, `TokenService`)

**Files:**
- Create: `apps/backend/src/Infrastructure/Identity/JwtOptions.cs`
- Create: `apps/backend/src/Infrastructure/Identity/ITokenService.cs`
- Create: `apps/backend/src/Infrastructure/Identity/TokenService.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Identity/TokenServiceTests.cs`

**Interfaces:**
- Consumes: `ApplicationUser`, `TimeProvider`.
- Produces:
  - `JwtOptions`: `Issuer`, `Audience`, `SecretKey`, `AccessTokenExpirationMinutes`, `RefreshTokenExpirationDays`.
  - `ITokenService`:
    - `string GenerateAccessToken(ApplicationUser user)`
    - `(string RawToken, string TokenHash) GenerateRefreshToken()`
    - `string HashToken(string rawToken)`

- [ ] **Step 1: Write failing unit test for `TokenService`**

Create `apps/backend/tests/ExamPrism.Tests/Identity/TokenServiceTests.cs`:
```csharp
namespace ExamPrism.Tests.Identity;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class TokenServiceTests
{
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly IOptions<JwtOptions> _options = Options.Create(new JwtOptions
    {
        Issuer = "ExamPrismTest",
        Audience = "ExamPrismTestAudience",
        SecretKey = "SuperSecretKeyForTestingJwtTokensMin256BitsLong!",
        AccessTokenExpirationMinutes = 15,
        RefreshTokenExpirationDays = 7
    });

    [Fact]
    public void GenerateAccessToken_ContainsExpectedClaimsAndExpiration()
    {
        var service = new TokenService(_options, _timeProvider);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            UserName = "user@example.com"
        };

        var tokenString = service.GenerateAccessToken(user);
        tokenString.Should().NotBeNullOrWhiteSpace();

        var handler = new JwtSecurityTokenHandler();
        var jwt = handler.ReadJwtToken(tokenString);

        jwt.Issuer.Should().Be("ExamPrismTest");
        jwt.Audiences.Should().Contain("ExamPrismTestAudience");
        jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value.Should().Be(user.Id.ToString());
        jwt.Claims.First(c => c.Type == JwtRegisteredClaimNames.Email).Value.Should().Be(user.Email);
        jwt.ValidTo.Should().BeCloseTo(_timeProvider.GetUtcNow().UtcDateTime.AddMinutes(15), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void GenerateRefreshToken_ReturnsRawTokenAndMatchingDeterministicSha256Hash()
    {
        var service = new TokenService(_options, _timeProvider);

        var (rawToken, tokenHash) = service.GenerateRefreshToken();

        rawToken.Should().NotBeNullOrWhiteSpace();
        tokenHash.Should().NotBeNullOrWhiteSpace();
        service.HashToken(rawToken).Should().Be(tokenHash);
    }
}
```

- [ ] **Step 2: Run test to verify it fails compilation**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter FullyQualifiedName~TokenServiceTests`  
Expected: Compilation failure (types do not exist yet).

- [ ] **Step 3: Implement `JwtOptions`, `ITokenService`, and `TokenService`**

Create `apps/backend/src/Infrastructure/Identity/JwtOptions.cs`:
```csharp
namespace ExamPrism.Infrastructure.Identity;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public int AccessTokenExpirationMinutes { get; set; } = 15;
    public int RefreshTokenExpirationDays { get; set; } = 7;
}
```

Create `apps/backend/src/Infrastructure/Identity/ITokenService.cs`:
```csharp
namespace ExamPrism.Infrastructure.Identity;

public interface ITokenService
{
    string GenerateAccessToken(ApplicationUser user);
    (string RawToken, string TokenHash) GenerateRefreshToken();
    string HashToken(string rawToken);
}
```

Create `apps/backend/src/Infrastructure/Identity/TokenService.cs`:
```csharp
namespace ExamPrism.Infrastructure.Identity;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

public class TokenService(IOptions<JwtOptions> options, TimeProvider timeProvider) : ITokenService
{
    private readonly JwtOptions _options = options.Value;

    public string GenerateAccessToken(ApplicationUser user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_options.AccessTokenExpirationMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public (string RawToken, string TokenHash) GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomBytes);

        var rawToken = Convert.ToBase64String(randomBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        var tokenHash = HashToken(rawToken);
        return (rawToken, tokenHash);
    }

    public string HashToken(string rawToken)
    {
        var bytes = Encoding.UTF8.GetBytes(rawToken);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter FullyQualifiedName~TokenServiceTests`  
Expected: PASS (2 tests passed).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/Identity/JwtOptions.cs apps/backend/src/Infrastructure/Identity/ITokenService.cs apps/backend/src/Infrastructure/Identity/TokenService.cs apps/backend/tests/ExamPrism.Tests/Identity/TokenServiceTests.cs
git commit -m "feat(identity): implement JwtOptions and TokenService with CSPRNG and SHA-256"
```

---

### Task 3: Infrastructure DI & Program Authentication Pipeline

**Files:**
- Modify: `apps/backend/src/Infrastructure/DependencyInjection.cs`
- Modify: `apps/backend/src/Program.cs`
- Modify: `apps/backend/src/appsettings.json`
- Modify: `apps/backend/src/appsettings.Development.json`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`

**Interfaces:**
- Consumes: `AppDbContext`, `ApplicationUser`, `JwtOptions`, `ITokenService`.
- Produces:
  - Identity registered via `AddIdentityCore<ApplicationUser>`.
  - Authentication configured with `JwtBearerDefaults.AuthenticationScheme` and `OnMessageReceived` cookie fallback.
  - Authorization configured.
  - `Program.cs` calling `app.UseAuthentication()` and `app.UseAuthorization()`.

- [ ] **Step 1: Write failing test in `DependencyInjectionTests.cs` for Identity and TokenService resolution**

Update `apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs`:
Add tests asserting that `UserManager<ApplicationUser>` and `ITokenService` resolve from service provider.
```csharp
[Fact]
public void AddInfrastructure_RegistersIdentityAndTokenService()
{
    // Arrange
    var inMemorySettings = new Dictionary<string, string?>
    {
        { "ConnectionStrings:DefaultConnection", "Host=localhost;Database=test;Username=postgres;Password=postgres" },
        { "Jwt:Issuer", "ExamPrism" },
        { "Jwt:Audience", "ExamPrism" },
        { "Jwt:SecretKey", "SuperSecretKeyForTestingJwtTokensMin256BitsLong!" }
    };
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(inMemorySettings).Build();
    var services = new ServiceCollection();

    // Act
    services.AddInfrastructure(configuration);
    var provider = services.BuildServiceProvider();

    // Assert
    provider.GetService<ITokenService>().Should().NotBeNull();
    provider.GetService<UserManager<ApplicationUser>>().Should().NotBeNull();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter FullyQualifiedName~RegistersIdentityAndTokenService`  
Expected: FAIL (Services not registered).

- [ ] **Step 3: Update `DependencyInjection.cs`, `Program.cs`, and `appsettings.json`**

In `apps/backend/src/appsettings.json` and `appsettings.Development.json`:
Add `"Jwt"` section:
```json
"Jwt": {
  "Issuer": "ExamPrism",
  "Audience": "ExamPrism",
  "SecretKey": "SuperSecretKeyForDevJwtTokensMin256BitsLongExamPrismDevOnly!",
  "AccessTokenExpirationMinutes": 15,
  "RefreshTokenExpirationDays": 7
}
```

In `apps/backend/src/Infrastructure/DependencyInjection.cs`:
Add:
```csharp
// Identity setup
services.AddIdentityCore<ApplicationUser>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequiredLength = 6;
    options.User.RequireUniqueEmail = true;
})
.AddRoles<IdentityRole<Guid>>()
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// JwtOptions & TokenService
services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
services.AddScoped<ITokenService, TokenService>();

var jwtOptions = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt configuration section is missing.");

services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtOptions.Issuer,
        ValidAudience = jwtOptions.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SecretKey)),
        ClockSkew = TimeSpan.Zero
    };

    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            if (string.IsNullOrEmpty(context.Token) &&
                context.Request.Cookies.TryGetValue("access_token", out var cookieToken))
            {
                context.Token = cookieToken;
            }
            return Task.CompletedTask;
        }
    };
});

services.AddAuthorization();
```

In `apps/backend/src/Program.cs`:
Add after `app.MapOpenApi()`:
```csharp
app.UseAuthentication();
app.UseAuthorization();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj`  
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Infrastructure/DependencyInjection.cs apps/backend/src/Program.cs apps/backend/src/appsettings.json apps/backend/src/appsettings.Development.json apps/backend/tests/ExamPrism.Tests/Infrastructure/DependencyInjectionTests.cs
git commit -m "feat(auth): configure Identity, JwtBearer with cookie support, and authorization in DI"
```

---

### Task 4: Auth DTOs & Cookie Helper

**Files:**
- Create: `apps/backend/src/Endpoints/Auth/AuthDtos.cs`
- Create: `apps/backend/src/Endpoints/Auth/AuthCookieHelper.cs`
- Test: `apps/backend/tests/ExamPrism.Tests/Endpoints/AuthCookieHelperTests.cs`

**Interfaces:**
- Produces:
  - `RegisterRequest(string Email, string Password)`
  - `LoginRequest(string Email, string Password)`
  - `UserResponse(Guid Id, string Email)`
  - `AuthCookieHelper`:
    - `SetAuthCookies(HttpResponse response, string accessToken, string rawRefreshToken, JwtOptions options, TimeProvider timeProvider)`
    - `ClearAuthCookies(HttpResponse response)`

- [ ] **Step 1: Write failing unit test for `AuthCookieHelper`**

Create `apps/backend/tests/ExamPrism.Tests/Endpoints/AuthCookieHelperTests.cs`:
```csharp
namespace ExamPrism.Tests.Endpoints;

using ExamPrism.Endpoints.Auth;
using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class AuthCookieHelperTests
{
    [Fact]
    public void SetAuthCookies_SetsBothCookiesWithHttpOnlyAndPath()
    {
        var context = new DefaultHttpContext();
        var options = new JwtOptions { AccessTokenExpirationMinutes = 15, RefreshTokenExpirationDays = 7 };
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

        AuthCookieHelper.SetAuthCookies(context.Response, "access-token-val", "refresh-token-val", options, timeProvider);

        var setCookieHeaders = context.Response.Headers.SetCookie.ToArray();
        setCookieHeaders.Should().HaveCount(2);

        var accessCookie = setCookieHeaders.First(h => h.StartsWith("access_token="));
        accessCookie.Should().Contain("httponly");
        accessCookie.Should().Contain("path=/");
        accessCookie.Should().Contain("samesite=lax");

        var refreshCookie = setCookieHeaders.First(h => h.StartsWith("refresh_token="));
        refreshCookie.Should().Contain("httponly");
        refreshCookie.Should().Contain("path=/");
        refreshCookie.Should().Contain("samesite=lax");
    }

    [Fact]
    public void ClearAuthCookies_DeletesBothCookies()
    {
        var context = new DefaultHttpContext();

        AuthCookieHelper.ClearAuthCookies(context.Response);

        var setCookieHeaders = context.Response.Headers.SetCookie.ToArray();
        setCookieHeaders.Should().HaveCount(2);
        setCookieHeaders.Should().Contain(h => h.StartsWith("access_token=") && (h.Contains("expires=") || h.Contains("max-age=0")));
        setCookieHeaders.Should().Contain(h => h.StartsWith("refresh_token=") && (h.Contains("expires=") || h.Contains("max-age=0")));
    }
}
```

- [ ] **Step 2: Run test to verify it fails compilation**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter FullyQualifiedName~AuthCookieHelperTests`  
Expected: Compilation failure.

- [ ] **Step 3: Implement `AuthDtos.cs` and `AuthCookieHelper.cs`**

Create `apps/backend/src/Endpoints/Auth/AuthDtos.cs`:
```csharp
namespace ExamPrism.Endpoints.Auth;

using System.ComponentModel.DataAnnotations;

public record RegisterRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(6)] string Password);

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public record UserResponse(Guid Id, string Email);
```

Create `apps/backend/src/Endpoints/Auth/AuthCookieHelper.cs`:
```csharp
namespace ExamPrism.Endpoints.Auth;

using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;

public static class AuthCookieHelper
{
    public const string AccessTokenCookieName = "access_token";
    public const string RefreshTokenCookieName = "refresh_token";

    public static void SetAuthCookies(
        HttpResponse response,
        string accessToken,
        string rawRefreshToken,
        JwtOptions options,
        TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();

        response.Cookies.Append(AccessTokenCookieName, accessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = now.AddMinutes(options.AccessTokenExpirationMinutes)
        });

        response.Cookies.Append(RefreshTokenCookieName, rawRefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = now.AddDays(options.RefreshTokenExpirationDays)
        });
    }

    public static void ClearAuthCookies(HttpResponse response)
    {
        response.Cookies.Delete(AccessTokenCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });

        response.Cookies.Delete(RefreshTokenCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter FullyQualifiedName~AuthCookieHelperTests`  
Expected: PASS (2 tests passed).

- [ ] **Step 5: Commit**

```bash
git add apps/backend/src/Endpoints/Auth/AuthDtos.cs apps/backend/src/Endpoints/Auth/AuthCookieHelper.cs apps/backend/tests/ExamPrism.Tests/Endpoints/AuthCookieHelperTests.cs
git commit -m "feat(auth): add AuthDtos and AuthCookieHelper"
```

---

### Task 5: Auth Endpoints Implementation (`AuthEndpoints.cs`)

**Files:**
- Create: `apps/backend/src/Endpoints/Auth/AuthEndpoints.cs`
- Modify: `apps/backend/src/Infrastructure/Persistence/AppDbContext.cs` (ensure migrations / DB creation support)

**Interfaces:**
- Consumes: `IEndpoint`, `UserManager<ApplicationUser>`, `AppDbContext`, `ITokenService`, `AuthCookieHelper`, `JwtOptions`, `TimeProvider`.
- Produces:
  - Route group: `/api/auth`
    - `POST /register`: Registers user, sets cookies, returns `201 Created` with `UserResponse`.
    - `POST /login`: Validates password, sets cookies, returns `200 OK` with `UserResponse`.
    - `POST /refresh`: Reads cookie, checks reuse, sets rotated cookies, returns `200 OK`.
    - `POST /logout`: Revokes token, deletes cookies, returns `204 No Content`.
    - `GET /me`: Authenticated endpoint, returns `200 OK` with `UserResponse`.

- [ ] **Step 1: Implement `AuthEndpoints.cs`**

Create `apps/backend/src/Endpoints/Auth/AuthEndpoints.cs`:
```csharp
namespace ExamPrism.Endpoints.Auth;

using System.Security.Claims;
using Common.Endpoints;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

public class AuthEndpoints : IEndpoint
{
    public void MapEndpoint(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication");

        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/refresh", RefreshAsync);
        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/me", GetMeAsync).RequireAuthorization();
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        ITokenService tokenService,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider,
        HttpResponse response)
    {
        var existing = await userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                { nameof(request.Email), ["Email is already registered."] }
            });
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = request.Email,
            UserName = request.Email
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            var errors = result.Errors
                .GroupBy(e => e.Code)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
            return Results.ValidationProblem(errors);
        }

        var accessToken = tokenService.GenerateAccessToken(user);
        var (rawRefresh, refreshHash) = tokenService.GenerateRefreshToken();

        var now = timeProvider.GetUtcNow();
        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenExpirationDays)
        };

        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync();

        AuthCookieHelper.SetAuthCookies(response, accessToken, rawRefresh, jwtOptions.Value, timeProvider);

        return Results.Created($"/api/auth/me", new UserResponse(user.Id, user.Email!));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        ITokenService tokenService,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider,
        HttpResponse response)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
        {
            return Results.Unauthorized();
        }

        var accessToken = tokenService.GenerateAccessToken(user);
        var (rawRefresh, refreshHash) = tokenService.GenerateRefreshToken();

        var now = timeProvider.GetUtcNow();
        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenExpirationDays)
        };

        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync();

        AuthCookieHelper.SetAuthCookies(response, accessToken, rawRefresh, jwtOptions.Value, timeProvider);

        return Results.Ok(new UserResponse(user.Id, user.Email!));
    }

    private static async Task<IResult> RefreshAsync(
        HttpRequest request,
        HttpResponse response,
        AppDbContext db,
        ITokenService tokenService,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider)
    {
        if (!request.Cookies.TryGetValue(AuthCookieHelper.RefreshTokenCookieName, out var rawRefresh) ||
            string.IsNullOrWhiteSpace(rawRefresh))
        {
            return Results.Unauthorized();
        }

        var hash = tokenService.HashToken(rawRefresh);
        var token = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash);

        if (token is null)
        {
            AuthCookieHelper.ClearAuthCookies(response);
            return Results.Unauthorized();
        }

        var now = timeProvider.GetUtcNow();

        // Compromised reuse detection
        if (token.IsRevoked || token.ReplacedByTokenHash != null)
        {
            var activeTokens = await db.RefreshTokens
                .Where(t => t.UserId == token.UserId && t.RevokedAt == null)
                .ToListAsync();

            foreach (var t in activeTokens)
            {
                t.RevokedAt = now;
            }

            await db.SaveChangesAsync();
            AuthCookieHelper.ClearAuthCookies(response);
            return Results.Unauthorized();
        }

        // Expired check
        if (token.IsExpired(timeProvider))
        {
            AuthCookieHelper.ClearAuthCookies(response);
            return Results.Unauthorized();
        }

        // Rotate
        var (newRaw, newHash) = tokenService.GenerateRefreshToken();
        var newAccessToken = tokenService.GenerateAccessToken(token.User);

        token.RevokedAt = now;
        token.ReplacedByTokenHash = newHash;

        var newToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = token.UserId,
            TokenHash = newHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenExpirationDays)
        };

        db.RefreshTokens.Add(newToken);
        await db.SaveChangesAsync();

        AuthCookieHelper.SetAuthCookies(response, newAccessToken, newRaw, jwtOptions.Value, timeProvider);

        return Results.Ok();
    }

    private static async Task<IResult> LogoutAsync(
        HttpRequest request,
        HttpResponse response,
        AppDbContext db,
        ITokenService tokenService,
        TimeProvider timeProvider)
    {
        if (request.Cookies.TryGetValue(AuthCookieHelper.RefreshTokenCookieName, out var rawRefresh) &&
            !string.IsNullOrWhiteSpace(rawRefresh))
        {
            var hash = tokenService.HashToken(rawRefresh);
            var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (token is not null && !token.IsRevoked)
            {
                token.RevokedAt = timeProvider.GetUtcNow();
                await db.SaveChangesAsync();
            }
        }

        AuthCookieHelper.ClearAuthCookies(response);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMeAsync(
        ClaimsPrincipal principal,
        UserManager<ApplicationUser> userManager)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var guid))
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(guid.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new UserResponse(user.Id, user.Email!));
    }
}
```

- [ ] **Step 2: Build solution to ensure clean compilation**

Run: `dotnet build apps/backend/ExamPrism.slnx`  
Expected: Build succeeded with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add apps/backend/src/Endpoints/Auth/AuthEndpoints.cs
git commit -m "feat(auth): implement register, login, refresh with rotation, logout, and me endpoints"
```

---

### Task 6: Comprehensive Integration Tests (`AuthEndpointTests.cs`)

**Files:**
- Create: `apps/backend/tests/ExamPrism.Tests/Endpoints/AuthEndpointTests.cs`
- Modify: `apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs` (ensure schema creation with `EnsureCreatedAsync()`)

**Interfaces:**
- Exercises:
  - All 5 endpoints via real HTTP requests on PostgreSQL Testcontainers.
  - Review Focus 1: Reuse detection revoking all user tokens.
  - Review Focus 2: Expired access token returns 401 on `/api/auth/me`.
  - Review Focus 3: `Authorization: Bearer` fallback works without cookie.
  - Review Focus 4: Duplicate email returns 400.
  - Review Focus 5: Logout clears cookies and marks token revoked.

- [ ] **Step 1: Ensure DB schema creation in `CustomWebApplicationFactory`**

Verify and ensure `CustomWebApplicationFactory` calls `context.Database.EnsureCreatedAsync()` after starting the container so Identity and `RefreshToken` tables exist during test execution.

- [ ] **Step 2: Write tests in `apps/backend/tests/ExamPrism.Tests/Endpoints/AuthEndpointTests.cs`**

Write test methods:
1. `Register_ValidInput_Returns201AndSetsCookiesAndCreatesUserInDb`
2. `Register_DuplicateEmail_Returns400ValidationProblem`
3. `Login_ValidCredentials_Returns200AndSetsCookies`
4. `Login_InvalidPassword_Returns401`
5. `Refresh_ValidCookie_RotatesTokensAndSetsNewCookies`
6. `Refresh_ReusedRevokedToken_TriggersCompromiseDetectionAndRevokesAllTokens`
7. `Logout_WithValidRefreshToken_RevokesTokenAndClearsCookies`
8. `GetMe_Unauthenticated_Returns401`
9. `GetMe_WithValidCookie_Returns200AndUser`
10. `GetMe_WithBearerHeader_Returns200AndUser`

- [ ] **Step 3: Run integration test suite**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj --filter FullyQualifiedName~AuthEndpointTests`  
Expected: PASS (all tests pass on PostgreSQL testcontainer).

- [ ] **Step 4: Run full backend test suite**

Run: `dotnet test apps/backend/tests/ExamPrism.Tests/ExamPrism.Tests.csproj`  
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add apps/backend/tests/ExamPrism.Tests/apps/backend/tests/ExamPrism.Tests/Infrastructure/CustomWebApplicationFactory.cs apps/backend/tests/ExamPrism.Tests/Endpoints/AuthEndpointTests.cs
git commit -m "test(auth): add integration tests for auth endpoints and token rotation on PostgreSQL"
```
