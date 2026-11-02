namespace ExamPrism.Tests.Features;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using ExamPrism.Endpoints.Auth;
using ExamPrism.Features.Auth;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using global::Tests.Infrastructure;
using Xunit;

public class AuthEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AuthEndpointTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static (string? AccessToken, string? RefreshToken) ExtractCookies(HttpResponseMessage response)
    {
        string? access = null;
        string? refresh = null;

        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var cookie in cookies)
            {
                if (cookie.StartsWith("access_token="))
                {
                    access = cookie.Split(';')[0]["access_token=".Length..];
                }
                else if (cookie.StartsWith("refresh_token="))
                {
                    refresh = cookie.Split(';')[0]["refresh_token=".Length..];
                }
            }
        }

        return (access, refresh);
    }

    [Fact]
    public async Task Register_ValidInput_Returns201AndSetsCookiesAndCreatesUserInDb()
    {
        var email = $"newuser_{Guid.NewGuid()}@example.com";
        var request = new RegisterCommand(email, "Password123!");

        var response = await _client.PostAsJsonAsync("/api/auth/register", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var user = await response.Content.ReadFromJsonAsync<RegisterResult>();
        user.Should().NotBeNull();
        user!.Email.Should().Be(email);

        var (access, refresh) = ExtractCookies(response);
        access.Should().NotBeNullOrWhiteSpace();
        refresh.Should().NotBeNullOrWhiteSpace();

        using var scope = await _factory.CreateTenantScopeAsync("tenant-a");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbUser = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
        dbUser.Should().NotBeNull();
        var tokens = await db.RefreshTokens.Where(t => t.UserId == dbUser!.Id).ToListAsync();
        tokens.Should().HaveCount(1);
    }

    [Fact]
    public async Task Register_DuplicateEmail_Returns400ValidationProblem()
    {
        var email = $"duplicate_{Guid.NewGuid()}@example.com";
        var request = new RegisterCommand(email, "Password123!");

        var firstResponse = await _client.PostAsJsonAsync("/api/auth/register", request);
        firstResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var secondResponse = await _client.PostAsJsonAsync("/api/auth/register", request);
        secondResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_ValidCredentials_Returns200AndSetsCookies()
    {
        var email = $"loginuser_{Guid.NewGuid()}@example.com";
        var password = "Password123!";
        await _client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, password));

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginCommand(email, password));

        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await loginResponse.Content.ReadFromJsonAsync<LoginResult>();
        user.Should().NotBeNull();
        user!.Email.Should().Be(email);

        var (access, refresh) = ExtractCookies(loginResponse);
        access.Should().NotBeNullOrWhiteSpace();
        refresh.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Login_InvalidPassword_Returns401()
    {
        var email = $"loginfail_{Guid.NewGuid()}@example.com";
        await _client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, "Password123!"));

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginCommand(email, "WrongPassword!"));

        loginResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_ValidCookie_RotatesTokensAndSetsNewCookies()
    {
        var email = $"refreshuser_{Guid.NewGuid()}@example.com";
        var regResponse = await _client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, "Password123!"));
        var (_, rawRefresh) = ExtractCookies(regResponse);
        rawRefresh.Should().NotBeNullOrWhiteSpace();

        var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        refreshRequest.Headers.Add("Cookie", $"refresh_token={rawRefresh}");

        var refreshResponse = await _client.SendAsync(refreshRequest);

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var (newAccess, newRefresh) = ExtractCookies(refreshResponse);
        newAccess.Should().NotBeNullOrWhiteSpace();
        newRefresh.Should().NotBeNullOrWhiteSpace();
        newRefresh.Should().NotBe(rawRefresh);
    }

    [Fact]
    public async Task Refresh_ReusedRevokedToken_TriggersCompromiseDetectionAndRevokesAllTokens()
    {
        var email = $"reuseuser_{Guid.NewGuid()}@example.com";
        var regResponse = await _client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, "Password123!"));
        var (user, rawRefresh1) = (await regResponse.Content.ReadFromJsonAsync<RegisterResult>(), ExtractCookies(regResponse).RefreshToken);

        // First rotation: valid
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req1.Headers.Add("Cookie", $"refresh_token={rawRefresh1}");
        var res1 = await _client.SendAsync(req1);
        res1.StatusCode.Should().Be(HttpStatusCode.OK);
        var (_, rawRefresh2) = ExtractCookies(res1);

        // Replay of old rawRefresh1: should trigger reuse detection and revoke rawRefresh2 as well!
        var reqReuse = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        reqReuse.Headers.Add("Cookie", $"refresh_token={rawRefresh1}");
        var resReuse = await _client.SendAsync(reqReuse);

        resReuse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Verify in DB that ALL tokens for user are now revoked
        using var scope = await _factory.CreateTenantScopeAsync("tenant-a");
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userTokens = await db.RefreshTokens.Where(t => t.UserId == user!.Id).ToListAsync();
        userTokens.Should().NotBeEmpty();
        userTokens.Should().OnlyContain(t => t.RevokedAt != null);

        // And rawRefresh2 is now rejected as well
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        req2.Headers.Add("Cookie", $"refresh_token={rawRefresh2}");
        var res2 = await _client.SendAsync(req2);
        res2.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_WithValidRefreshToken_RevokesTokenAndClearsCookies()
    {
        var email = $"logoutuser_{Guid.NewGuid()}@example.com";
        var regResponse = await _client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, "Password123!"));
        var (_, rawRefresh) = ExtractCookies(regResponse);

        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        logoutRequest.Headers.Add("Cookie", $"refresh_token={rawRefresh}");

        var logoutResponse = await _client.SendAsync(logoutRequest);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Set-Cookie should clear cookies
        logoutResponse.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue();
        var cookieList = setCookies!.ToList();
        cookieList.Should().Contain(c => c.StartsWith("access_token=") && (c.Contains("max-age=0") || c.Contains("expires=")));
        cookieList.Should().Contain(c => c.StartsWith("refresh_token=") && (c.Contains("max-age=0") || c.Contains("expires=")));
    }

    [Fact]
    public async Task GetMe_Unauthenticated_Returns401()
    {
        var response = await _client.GetAsync("/api/auth/me");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMe_WithValidCookie_Returns200AndUser()
    {
        var email = $"getmeuser_{Guid.NewGuid()}@example.com";
        var regResponse = await _client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, "Password123!"));
        var (accessToken, _) = ExtractCookies(regResponse);

        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Add("Cookie", $"access_token={accessToken}");

        var meResponse = await _client.SendAsync(meRequest);

        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await meResponse.Content.ReadFromJsonAsync<GetMeResult>();
        user.Should().NotBeNull();
        user!.Email.Should().Be(email);
    }

    [Fact]
    public async Task GetMe_WithBearerHeader_Returns200AndUser()
    {
        var email = $"beareruser_{Guid.NewGuid()}@example.com";
        var regResponse = await _client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, "Password123!"));
        var (accessToken, _) = ExtractCookies(regResponse);

        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var meResponse = await _client.SendAsync(meRequest);

        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await meResponse.Content.ReadFromJsonAsync<GetMeResult>();
        user.Should().NotBeNull();
        user!.Email.Should().Be(email);
    }

    [Theory]
    [InlineData("", "Password123!")]
    [InlineData("test@example.com", "")]
    [InlineData(null, "Password123!")]
    public async Task Register_WithEmptyOrNullInput_Returns400(string? email, string? password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email!, password!));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("", "Password123!")]
    [InlineData("test@example.com", "")]
    [InlineData(null, "Password123!")]
    public async Task Login_WithEmptyOrNullInput_Returns400(string? email, string? password)
    {
        var response = await _client.PostAsJsonAsync("/api/auth/login", new LoginCommand(email!, password!));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refresh_WithExpiredToken_Returns401AndClearsCookies()
    {
        var email = $"expiredrefresh_{Guid.NewGuid()}@example.com";
        var regResponse = await _client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, "Password123!"));
        var (_, rawRefresh) = ExtractCookies(regResponse);

        using (var scope = await _factory.CreateTenantScopeAsync("tenant-a"))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
            var hash = tokenService.HashToken(rawRefresh!);
            var dbToken = await db.RefreshTokens.FirstAsync(t => t.TokenHash == hash);
            dbToken.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        refreshRequest.Headers.Add("Cookie", $"refresh_token={rawRefresh}");

        var refreshResponse = await _client.SendAsync(refreshRequest);

        refreshResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        refreshResponse.Headers.TryGetValues("Set-Cookie", out var setCookies).Should().BeTrue();
        var cookieList = setCookies!.ToList();
        cookieList.Should().Contain(c => c.StartsWith("access_token=") && (c.Contains("max-age=0") || c.Contains("expires=")));
        cookieList.Should().Contain(c => c.StartsWith("refresh_token=") && (c.Contains("max-age=0") || c.Contains("expires=")));
    }

    [Fact]
    public async Task GetMe_WithExpiredAccessToken_Returns401()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("SuperSecretKeyForDevJwtTokensMin256BitsLongExamPrismDevOnly!"));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var expiredJwt = new JwtSecurityToken(
            issuer: "ExamPrism",
            audience: "ExamPrism",
            claims: [new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())],
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: DateTime.UtcNow.AddHours(-1),
            signingCredentials: creds);
        var expiredToken = new JwtSecurityTokenHandler().WriteToken(expiredJwt);

        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meRequest.Headers.Add("Cookie", $"access_token={expiredToken}");

        var meResponse = await _client.SendAsync(meRequest);

        meResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
