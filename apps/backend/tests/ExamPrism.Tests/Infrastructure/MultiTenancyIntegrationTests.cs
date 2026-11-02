namespace Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Common.Results;
using ExamPrism.Features.Auth;
using ExamPrism.Features.Health;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;

public class MultiTenancyIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MultiTenancyIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
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
    public async Task PhysicalDatabaseIsolation_SameEmailRegistersIndependentlyAcrossTenantAAndTenantB_WithSeparateCredentials()
    {
        var clientA = _factory.CreateTenantClient("tenant-a");
        var clientB = _factory.CreateTenantClient("tenant-b");

        var email = $"alice_{Guid.NewGuid()}@school.edu";
        var passwordA = "PasswordTenantA1!";
        var passwordB = "PasswordTenantB2!";

        // 1. Register same email independently on both tenant-a and tenant-b
        var regA = await clientA.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, passwordA));
        regA.StatusCode.Should().Be(HttpStatusCode.Created);

        var regB = await clientB.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, passwordB));
        regB.StatusCode.Should().Be(HttpStatusCode.Created);

        // 2. Logging into tenant-a with tenant-b password returns 401
        var loginAWrong = await clientA.PostAsJsonAsync("/api/auth/login", new LoginCommand(email, passwordB));
        loginAWrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 3. Logging into tenant-a with tenant-a password returns 200
        var loginACorrect = await clientA.PostAsJsonAsync("/api/auth/login", new LoginCommand(email, passwordA));
        loginACorrect.StatusCode.Should().Be(HttpStatusCode.OK);

        // 4. Logging into tenant-b with tenant-a password returns 401
        var loginBWrong = await clientB.PostAsJsonAsync("/api/auth/login", new LoginCommand(email, passwordA));
        loginBWrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 5. Logging into tenant-b with tenant-b password returns 200
        var loginBCorrect = await clientB.PostAsJsonAsync("/api/auth/login", new LoginCommand(email, passwordB));
        loginBCorrect.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task CrossTenantJwtRejection_BearerAndCookieFromTenantA_Return401TenantTokenMismatchOnTenantBAndPlatform()
    {
        var clientA = _factory.CreateTenantClient("tenant-a");
        var clientB = _factory.CreateTenantClient("tenant-b");
        var platformClient = _factory.CreatePlatformClient();

        var email = $"crosstenant_{Guid.NewGuid()}@school.edu";
        var password = "Password123!";

        var regRes = await clientA.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, password));
        regRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var (accessToken, _) = ExtractCookies(regRes);
        accessToken.Should().NotBeNullOrWhiteSpace();

        // 1. Send Bearer token to tenant-b -> 401 TENANT_TOKEN_MISMATCH
        var reqBearerTenantB = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        reqBearerTenantB.Headers.Add("Authorization", $"Bearer {accessToken}");
        var resBearerTenantB = await clientB.SendAsync(reqBearerTenantB);
        resBearerTenantB.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var bodyBearerTenantB = await resBearerTenantB.Content.ReadFromJsonAsync<JsonObject>();
        bodyBearerTenantB!["errorCode"]?.ToString().Should().Be("TENANT_TOKEN_MISMATCH");

        // 2. Send Cookie access_token to tenant-b -> 401 TENANT_TOKEN_MISMATCH
        var reqCookieTenantB = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        reqCookieTenantB.Headers.Add("Cookie", $"access_token={accessToken}");
        var resCookieTenantB = await clientB.SendAsync(reqCookieTenantB);
        resCookieTenantB.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var bodyCookieTenantB = await resCookieTenantB.Content.ReadFromJsonAsync<JsonObject>();
        bodyCookieTenantB!["errorCode"]?.ToString().Should().Be("TENANT_TOKEN_MISMATCH");

        // 3. Send token to platform root -> 401 TENANT_TOKEN_MISMATCH
        var reqBearerPlatform = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        reqBearerPlatform.Headers.Add("Authorization", $"Bearer {accessToken}");
        var resBearerPlatform = await platformClient.SendAsync(reqBearerPlatform);
        resBearerPlatform.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var bodyBearerPlatform = await resBearerPlatform.Content.ReadFromJsonAsync<JsonObject>();
        bodyBearerPlatform!["errorCode"]?.ToString().Should().Be("TENANT_TOKEN_MISMATCH");
    }

    [Theory]
    [InlineData("unknown", "en-US", HttpStatusCode.NotFound, "TENANT_NOT_FOUND", "The requested organization was not found.", null)]
    [InlineData("unknown", "vi-VN", HttpStatusCode.NotFound, "TENANT_NOT_FOUND", "Không tìm thấy tổ chức theo tên miền này.", null)]
    [InlineData("provisioning-org", "en-US", HttpStatusCode.ServiceUnavailable, "TENANT_PROVISIONING", "This organization workspace is currently being provisioned. Please try again shortly.", "5")]
    [InlineData("provisioning-org", "vi-VN", HttpStatusCode.ServiceUnavailable, "TENANT_PROVISIONING", "Không gian làm việc của tổ chức đang được khởi tạo. Vui lòng thử lại sau giây lát.", "5")]
    [InlineData("suspended-org", "en-US", HttpStatusCode.Forbidden, "TENANT_SUSPENDED", "This organization workspace has been suspended.", null)]
    [InlineData("suspended-org", "vi-VN", HttpStatusCode.Forbidden, "TENANT_SUSPENDED", "Không gian làm việc của tổ chức đã bị tạm khóa.", null)]
    [InlineData("failed-org", "en-US", HttpStatusCode.Forbidden, "TENANT_UNAVAILABLE", "This organization workspace is currently unavailable.", null)]
    [InlineData("failed-org", "vi-VN", HttpStatusCode.Forbidden, "TENANT_UNAVAILABLE", "Không gian làm việc của tổ chức hiện không khả dụng.", null)]
    public async Task TenantLifecycleGuards_Return404_503WithRetryAfter_And403_LocalizedInViAndEn(
        string slug,
        string culture,
        HttpStatusCode expectedStatus,
        string expectedErrorCode,
        string expectedDetail,
        string? expectedRetryAfter)
    {
        var client = _factory.CreateTenantClient(slug);
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Add("Accept-Language", culture);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(expectedStatus);

        if (expectedRetryAfter is not null)
        {
            response.Headers.TryGetValues("Retry-After", out var retryAfterValues).Should().BeTrue();
            retryAfterValues!.First().Should().Be(expectedRetryAfter);
        }

        var json = await response.Content.ReadFromJsonAsync<JsonObject>();
        json.Should().NotBeNull();
        json!["errorCode"]?.ToString().Should().Be(expectedErrorCode);
        json["detail"]?.ToString().Should().Be(expectedDetail);
    }

    [Fact]
    public async Task DomainBoundaries_PlatformRootRejectsTenantAuth400_ServesHealth200_AndTenantHostRejectsPlatformOnly404()
    {
        var platformClient = _factory.CreatePlatformClient();
        var tenantClient = _factory.CreateTenantClient("tenant-a");

        // 1. Platform root rejects POST /api/auth/login with 400 TENANT_REQUIRED
        var loginRes = await platformClient.PostAsJsonAsync("/api/auth/login", new LoginCommand("admin@platform.com", "Password123!"));
        loginRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var loginProblem = await loginRes.Content.ReadFromJsonAsync<JsonObject>();
        loginProblem!["errorCode"]?.ToString().Should().Be("TENANT_REQUIRED");

        // 2. Platform root serves /health with 200 Healthy
        var healthRes = await platformClient.GetAsync("/health");
        healthRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var healthBody = await healthRes.Content.ReadFromJsonAsync<GetHealthResult>();
        healthBody.Should().NotBeNull();
        healthBody!.Status.Should().Be("Healthy");

        // 3. Tenant host rejects /test/platform-only with 404 PLATFORM_ONLY
        var tenantProbeRes = await tenantClient.GetAsync("/test/platform-only");
        tenantProbeRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var tenantProbeProblem = await tenantProbeRes.Content.ReadFromJsonAsync<JsonObject>();
        tenantProbeProblem!["errorCode"]?.ToString().Should().Be("PLATFORM_ONLY");

        // 4. Platform host allows /test/platform-only with 200
        var platformProbeRes = await platformClient.GetAsync("/test/platform-only");
        platformProbeRes.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RateLimiting_ExhaustingAuthQuotaOnTenantA_DoesNotThrottleTenantB()
    {
        var limitedFactory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Auth:PermitLimit", "2");
            builder.UseSetting("RateLimiting:Auth:WindowSeconds", "60");
        });

        var clientA = limitedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://tenant-a.localhost")
        });

        var clientB = limitedFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://tenant-b.localhost")
        });

        var cmdA = new LoginCommand("limit-a@example.com", "WrongPassword!");
        var cmdB = new LoginCommand("limit-b@example.com", "WrongPassword!");

        // Request 1 on tenant-a -> 401
        var resA1 = await clientA.PostAsJsonAsync("/api/auth/login", cmdA);
        resA1.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Request 2 on tenant-a -> 401
        var resA2 = await clientA.PostAsJsonAsync("/api/auth/login", cmdA);
        resA2.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Request 3 on tenant-a -> 429 Too Many Requests
        var resA3 = await clientA.PostAsJsonAsync("/api/auth/login", cmdA);
        resA3.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Request 1 on tenant-b -> 401 Unauthorized (NOT 429!)
        var resB1 = await clientB.PostAsJsonAsync("/api/auth/login", cmdB);
        resB1.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WolverineBackgroundBridge_InvokeForTenantAsync_ExecutesHandlerAgainstTargetTenantDatabaseWithoutHttpContext()
    {
        var clientB = _factory.CreateTenantClient("tenant-b");

        var email = $"wolverine_{Guid.NewGuid()}@tenantb.edu";
        var password = "SecretPassword1!";

        // Register user on tenant-b
        var regRes = await clientB.PostAsJsonAsync("/api/auth/register", new RegisterCommand(email, password));
        regRes.StatusCode.Should().Be(HttpStatusCode.Created);

        // From non-HTTP scope (no HttpContext), invoke bus for tenant-b
        using var scope = _factory.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        var resultB = await bus.InvokeForTenantAsync<Result<LoginResult>>("tenant-b", new LoginCommand(email, password));
        resultB.IsSuccess.Should().BeTrue();
        resultB.Value.AccessToken.Should().NotBeNullOrWhiteSpace();

        // Invoking for tenant-a should fail with INVALID_CREDENTIALS (user does not exist in tenant-a's DB)
        var resultA = await bus.InvokeForTenantAsync<Result<LoginResult>>("tenant-a", new LoginCommand(email, password));
        resultA.IsFailure.Should().BeTrue();
        resultA.Error.Code.Should().Be("INVALID_CREDENTIALS");
    }
}
