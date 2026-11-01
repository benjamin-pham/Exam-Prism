# Design Specification: JWT Authentication with Refresh Token Rotation in HttpOnly Cookies

**Date:** 2026-11-01  
**Status:** Approved  
**Scope:** Architectural Subsystem  

---

## 1. Context and Goals

ExamPrism backend currently has database connectivity and health checks, but no authentication or user management. This specification defines the architecture, data models, endpoints, and security policies for:
1. User identity management using ASP.NET Core Identity (`IdentityDbContext`, `UserManager`).
2. Stateless JWT access tokens delivered via `HttpOnly`, `Secure` cookies (with fallback support for `Authorization: Bearer` headers).
3. Sliding refresh token rotation with reuse detection stored in PostgreSQL and delivered via `HttpOnly`, `Secure` cookies.
4. Core authentication endpoints: `/api/auth/register`, `/api/auth/login`, `/api/auth/refresh`, `/api/auth/logout`, and `/api/auth/me`.

---

## 2. Architecture & Tech Stack

* **Platform:** .NET 10 Minimal API with `IEndpoint` architecture.
* **Identity Management:** `Microsoft.AspNetCore.Identity.EntityFrameworkCore`.
* **Database & ORM:** PostgreSQL with Entity Framework Core (`Npgsql.EntityFrameworkCore.PostgreSQL`).
* **Authentication:** `Microsoft.AspNetCore.Authentication.JwtBearer`.
* **Time Provider:** Standard .NET `TimeProvider` (registered as singleton, mockable in tests via `FakeTimeProvider`).
* **Testing:** xUnit, FluentAssertions, NSubstitute, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`.

---

## 3. Data Models

### 3.1. `ApplicationUser`
Inherits from `IdentityUser<Guid>`:
```csharp
public class ApplicationUser : IdentityUser<Guid>
{
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
```

### 3.2. `RefreshToken`
Stores persisted refresh tokens with SHA-256 hash security and lineage tracking:
```csharp
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

### 3.3. Database Context Configuration
`AppDbContext` updates from `DbContext` to `IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`:
* Primary key for users and roles is `Guid`.
* Table `RefreshTokens`:
  * Foreign key to `ApplicationUser` (`UserId`) with cascade delete.
  * Unique index on `TokenHash`.
  * Index on `UserId`.

---

## 4. Token & Security Design

### 4.1. Configuration (`JwtOptions`)
Mapped from `appsettings.json` section `"Jwt"`:
* `Issuer`: Token issuer string (e.g. `"ExamPrism"`).
* `Audience`: Token audience string (e.g. `"ExamPrism"`).
* `SecretKey`: Symmetric signing key (>= 256 bits).
* `AccessTokenExpirationMinutes`: Access token lifetime (default: 15 minutes).
* `RefreshTokenExpirationDays`: Refresh token lifetime (default: 7 days).

### 4.2. Token Service (`ITokenService`)
* `GenerateAccessToken(ApplicationUser user)`:
  * Generates signed JWT with claims:
    * `sub` / `ClaimTypes.NameIdentifier`: `user.Id.ToString()`
    * `email` / `ClaimTypes.Email`: `user.Email`
    * `jti`: New `Guid` identifier
  * Signs with HMAC-SHA256 (`SecurityAlgorithms.HmacSha256`).
* `GenerateRefreshToken()`:
  * Generates 64 bytes of cryptographically secure random bytes via `RandomNumberGenerator`.
  * Formats raw token as Base64Url string.
  * Computes deterministic SHA-256 hash of raw string for DB lookups (`TokenHash`).
  * Returns `(string RawToken, string TokenHash)`.

### 4.3. Cookie Policy
Cookies written by authentication endpoints:
* **`access_token`**:
  * `HttpOnly = true`
  * `Secure = true` (in production / HTTPS environments)
  * `SameSite = SameSiteMode.Lax`
  * `Path = "/"`
  * `Expires = UtcNow + AccessTokenExpirationMinutes`
* **`refresh_token`**:
  * `HttpOnly = true`
  * `Secure = true`
  * `SameSite = SameSiteMode.Lax`
  * `Path = "/"`
  * `Expires = UtcNow + RefreshTokenExpirationDays`

### 4.4. JWT Authentication Middleware
Configured with `AddJwtBearer`:
* `JwtBearerEvents.OnMessageReceived`:
  * If `Authorization` header is not present or empty, reads token from `context.Request.Cookies["access_token"]`.
* `TokenValidationParameters`:
  * `ValidateIssuer = true`
  * `ValidateAudience = true`
  * `ValidateLifetime = true`
  * `ValidateIssuerSigningKey = true`
  * `ClockSkew = TimeSpan.Zero`

### 4.5. Refresh Token Rotation & Reuse Detection
Executed on `POST /api/auth/refresh`:
1. Extract `refresh_token` from request cookie.
2. If absent or empty: return `401 Unauthorized`.
3. Compute SHA-256 hash of the received token.
4. Lookup `RefreshToken` in database by `TokenHash`, including navigation to `User`.
5. **Reuse Detection**:
   * If token found but `IsRevoked == true` OR `ReplacedByTokenHash != null`:
     * Compromise detected (token reuse).
     * Revoke **all** active tokens for this user (`RevokedAt = timeProvider.GetUtcNow()`).
     * Persist changes to database.
     * Clear both cookies from response.
     * Return `401 Unauthorized`.
6. **Expiration Check**:
   * If `IsExpired(timeProvider) == true`:
     * Clear cookies.
     * Return `401 Unauthorized`.
7. **Rotation Success**:
   * Generate new access token and new refresh token pair `(newRaw, newHash)`.
   * Update old token: `RevokedAt = timeProvider.GetUtcNow()`, `ReplacedByTokenHash = newHash`.
   * Create and insert new `RefreshToken` record.
   * Save changes to DB.
   * Attach updated cookies to HTTP response.
   * Return `200 OK`.

---

## 5. Endpoints Specification

Group: `/api/auth` (registered via `IEndpoint`)

### 5.1. `POST /api/auth/register`
* **Request:** `RegisterRequest(string Email, string Password)`
* **Validation:** Valid email format, non-empty password satisfying Identity password requirements.
* **Behavior:**
  * Checks if email already exists -> returns `400 Bad Request` (`ValidationProblem`).
  * Creates `ApplicationUser` via `UserManager.CreateAsync()`.
  * If failed -> returns `400 Bad Request` with Identity error descriptions.
  * If succeeded -> generates access token & refresh token, saves refresh token to DB, sets cookies.
* **Response:** `201 Created` with `UserResponse(Guid Id, string Email)`.

### 5.2. `POST /api/auth/login`
* **Request:** `LoginRequest(string Email, string Password)`
* **Behavior:**
  * Finds user by email.
  * Verifies password using `UserManager.CheckPasswordAsync()`.
  * If invalid user or password -> returns `401 Unauthorized` ("Invalid email or password").
  * If valid -> generates tokens, persists new refresh token, sets cookies.
* **Response:** `200 OK` with `UserResponse(Guid Id, string Email)`.

### 5.3. `POST /api/auth/refresh`
* **Request:** Empty body (reads `refresh_token` cookie).
* **Behavior:** Executes rotation and reuse detection.
* **Response:**
  * Success: `200 OK` with updated cookies.
  * Failure / Expired / Compromised: `401 Unauthorized` with cleared cookies.

### 5.4. `POST /api/auth/logout`
* **Request:** Empty body.
* **Behavior:**
  * Reads `refresh_token` cookie if present.
  * If token found in DB, marks `RevokedAt = timeProvider.GetUtcNow()`.
  * Clears `access_token` and `refresh_token` cookies on the response.
* **Response:** `204 No Content`.

### 5.5. `GET /api/auth/me`
* **Authorization:** `.RequireAuthorization()`
* **Behavior:**
  * Reads `ClaimTypes.NameIdentifier` / `sub` from `ClaimsPrincipal`.
  * Retrieves `ApplicationUser` by ID.
  * If user not found -> `404 Not Found`.
* **Response:** `200 OK` with `UserResponse(Guid Id, string Email)`.

---

## 6. Testing Strategy

### 6.1. Unit Tests
* `TokenServiceTests`:
  * Access token has correct claims, issuer, audience, and expiration.
  * Refresh token generation produces 64-byte random string and correct SHA-256 hash.
* `RefreshTokenTests`:
  * Correct evaluation of `IsActive`, `IsExpired`, and `IsRevoked` using `FakeTimeProvider`.

### 6.2. Integration Tests (`CustomWebApplicationFactory`)
* `AuthEndpointTests`:
  1. `Register_WithValidInput_Returns201AndSetsCookies`
  2. `Register_WithDuplicateEmail_Returns400`
  3. `Login_WithValidCredentials_Returns200AndSetsCookies`
  4. `Login_WithInvalidPassword_Returns401`
  5. `Refresh_WithValidToken_RotatesTokensAndSetsNewCookies`
  6. `Refresh_WithReusedToken_RevokesAllTokensAndReturns401`
  7. `Refresh_WithExpiredToken_Returns401AndClearsCookies`
  8. `Logout_RevokesTokenAndClearsCookies`
  9. `GetMe_Unauthenticated_Returns401`
  10. `GetMe_WithValidCookie_Returns200AndUserData`
  11. `GetMe_WithBearerHeader_Returns200AndUserData`
