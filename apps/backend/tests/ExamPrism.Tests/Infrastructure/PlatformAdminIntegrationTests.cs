namespace Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Common.MultiTenancy;
using ExamPrism.Features.Auth;
using ExamPrism.Features.Platform.Auth;
using ExamPrism.Features.Platform.Tenants;
using FluentAssertions;
using Xunit;

public class PlatformAdminIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PlatformAdminIntegrationTests(CustomWebApplicationFactory factory)
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
    public async Task PlatformAuthFlow_SuperAdminCanLoginGetMeRefreshAndLogout_OnPlatformHost()
    {
        var platformClient = _factory.CreatePlatformClient();

        // 1. SuperAdmin login on platform host -> 200 OK
        var loginResponse = await platformClient.PostAsJsonAsync("/api/platform/auth/login",
            new PlatformLoginCommand("admin@examprism.com", "AdminPassword123!"));
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var loginResult = await loginResponse.Content.ReadFromJsonAsync<PlatformLoginResult>();
        loginResult.Should().NotBeNull();
        loginResult!.Email.Should().Be("admin@examprism.com");
        loginResult.Roles.Should().Contain("SuperAdmin");

        var (accessToken, refreshToken) = ExtractCookies(loginResponse);
        accessToken.Should().NotBeNullOrWhiteSpace();
        refreshToken.Should().NotBeNullOrWhiteSpace();

        // 2. GET /api/platform/auth/me with access token -> 200 OK
        var meRequest = new HttpRequestMessage(HttpMethod.Get, "/api/platform/auth/me");
        meRequest.Headers.Add("Cookie", $"access_token={accessToken}");
        var meResponse = await platformClient.SendAsync(meRequest);
        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var meResult = await meResponse.Content.ReadFromJsonAsync<GetPlatformMeResult>();
        meResult.Should().NotBeNull();
        meResult!.Email.Should().Be("admin@examprism.com");
        meResult.Roles.Should().Contain("SuperAdmin");

        // 3. POST /api/platform/auth/refresh with refresh token -> 200 OK & rotates cookies
        var refreshRequest = new HttpRequestMessage(HttpMethod.Post, "/api/platform/auth/refresh");
        refreshRequest.Headers.Add("Cookie", $"refresh_token={refreshToken}");
        var refreshResponse = await platformClient.SendAsync(refreshRequest);
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var (rotatedAccess, rotatedRefresh) = ExtractCookies(refreshResponse);
        rotatedAccess.Should().NotBeNullOrWhiteSpace();
        rotatedRefresh.Should().NotBeNullOrWhiteSpace();
        rotatedRefresh.Should().NotBe(refreshToken);

        // 4. POST /api/platform/auth/logout with new refresh token -> 204 NoContent
        var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/platform/auth/logout");
        logoutRequest.Headers.Add("Cookie", $"refresh_token={rotatedRefresh}");
        var logoutResponse = await platformClient.SendAsync(logoutRequest);
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CrossBoundaryAndCrossTokenIsolation_RejectsPlatformRoutesOnTenantHost_TenantTokenOnPlatform_AndPlatformTokenOnTenant()
    {
        var tenantClient = _factory.CreateTenantClient("tenant-a");
        var platformClient = _factory.CreatePlatformClient();

        // 1. Tenant client calling platform routes -> 404 PLATFORM_ONLY
        var tenantLoginRes = await tenantClient.PostAsJsonAsync("/api/platform/auth/login",
            new PlatformLoginCommand("admin@examprism.com", "AdminPassword123!"));
        tenantLoginRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var tenantLoginProblem = await tenantLoginRes.Content.ReadFromJsonAsync<JsonObject>();
        tenantLoginProblem!["errorCode"]?.ToString().Should().Be("PLATFORM_ONLY");

        var tenantListRes = await tenantClient.GetAsync("/api/platform/tenants");
        tenantListRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var tenantListProblem = await tenantListRes.Content.ReadFromJsonAsync<JsonObject>();
        tenantListProblem!["errorCode"]?.ToString().Should().Be("PLATFORM_ONLY");

        // 2. Tenant user token calling platform API -> 401 TENANT_TOKEN_MISMATCH
        var tenantUserEmail = $"iso_tenant_user_{Guid.NewGuid()}@school.edu";
        var regRes = await tenantClient.PostAsJsonAsync("/api/auth/register",
            new RegisterCommand(tenantUserEmail, "Password123!"));
        regRes.StatusCode.Should().Be(HttpStatusCode.Created);

        var (tenantAccessToken, _) = ExtractCookies(regRes);
        tenantAccessToken.Should().NotBeNullOrWhiteSpace();

        var reqTenantOnPlatform = new HttpRequestMessage(HttpMethod.Get, "/api/platform/tenants");
        reqTenantOnPlatform.Headers.Add("Authorization", $"Bearer {tenantAccessToken}");
        var resTenantOnPlatform = await platformClient.SendAsync(reqTenantOnPlatform);
        resTenantOnPlatform.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var bodyTenantOnPlatform = await resTenantOnPlatform.Content.ReadFromJsonAsync<JsonObject>();
        bodyTenantOnPlatform!["errorCode"]?.ToString().Should().Be("TENANT_TOKEN_MISMATCH");

        // 3. SuperAdmin token calling tenant API -> 401 TENANT_TOKEN_MISMATCH
        var platLoginRes = await platformClient.PostAsJsonAsync("/api/platform/auth/login",
            new PlatformLoginCommand("admin@examprism.com", "AdminPassword123!"));
        platLoginRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var (platformAccessToken, _) = ExtractCookies(platLoginRes);
        platformAccessToken.Should().NotBeNullOrWhiteSpace();

        var reqPlatformOnTenant = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        reqPlatformOnTenant.Headers.Add("Authorization", $"Bearer {platformAccessToken}");
        var resPlatformOnTenant = await tenantClient.SendAsync(reqPlatformOnTenant);
        resPlatformOnTenant.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var bodyPlatformOnTenant = await resPlatformOnTenant.Content.ReadFromJsonAsync<JsonObject>();
        bodyPlatformOnTenant!["errorCode"]?.ToString().Should().Be("TENANT_TOKEN_MISMATCH");
    }

    [Fact]
    public async Task AdminTenantLifecycle_ListInspectUpdateSuspendAndReactivate_EvictsCacheImmediately()
    {
        var tenantClient = _factory.CreateTenantClient("tenant-a");
        var platformClient = _factory.CreatePlatformClient();

        // 1. Register & login user on tenant-a -> verifies 200 OK and primes CatalogMultiTenantStore cache
        var tenantUserEmail = $"lifecycle_user_{Guid.NewGuid()}@school.edu";
        var regRes = await tenantClient.PostAsJsonAsync("/api/auth/register",
            new RegisterCommand(tenantUserEmail, "Password123!"));
        regRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var (tenantAccessToken, _) = ExtractCookies(regRes);
        tenantAccessToken.Should().NotBeNullOrWhiteSpace();

        var warmReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        warmReq.Headers.Add("Authorization", $"Bearer {tenantAccessToken}");
        var warmRes = await tenantClient.SendAsync(warmReq);
        warmRes.StatusCode.Should().Be(HttpStatusCode.OK);

        // 2. SuperAdmin login on platform host
        var platLoginRes = await platformClient.PostAsJsonAsync("/api/platform/auth/login",
            new PlatformLoginCommand("admin@examprism.com", "AdminPassword123!"));
        platLoginRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var (platformAccessToken, _) = ExtractCookies(platLoginRes);
        platformAccessToken.Should().NotBeNullOrWhiteSpace();

        // 3. SuperAdmin lists tenants searching for tenant-a
        var searchReq = new HttpRequestMessage(HttpMethod.Get, "/api/platform/tenants?search=tenant-a");
        searchReq.Headers.Add("Authorization", $"Bearer {platformAccessToken}");
        var searchRes = await platformClient.SendAsync(searchReq);
        searchRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var listResult = await searchRes.Content.ReadFromJsonAsync<ListTenantsResult>();
        listResult.Should().NotBeNull();
        var tenantA = listResult!.Items.FirstOrDefault(t => t.Slug == "tenant-a");
        tenantA.Should().NotBeNull();
        tenantA!.Status.Should().Be(TenantStatus.Active);
        var tenantAId = tenantA.Id;

        // 4. SuperAdmin inspects tenant-a details
        var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/platform/tenants/{tenantAId}");
        getReq.Headers.Add("Authorization", $"Bearer {platformAccessToken}");
        var getRes = await platformClient.SendAsync(getReq);
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var tenantDetail = await getRes.Content.ReadFromJsonAsync<TenantDetailResult>();
        tenantDetail.Should().NotBeNull();
        tenantDetail!.Slug.Should().Be("tenant-a");
        tenantDetail.Status.Should().Be(TenantStatus.Active);

        // 5. SuperAdmin updates tenant-a Name
        var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/platform/tenants/{tenantAId}")
        {
            Content = JsonContent.Create(new UpdateTenantRequest("Tenant A Updated"))
        };
        patchReq.Headers.Add("Authorization", $"Bearer {platformAccessToken}");
        var patchRes = await platformClient.SendAsync(patchReq);
        patchRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var updatedDetail = await patchRes.Content.ReadFromJsonAsync<TenantDetailResult>();
        updatedDetail!.Name.Should().Be("Tenant A Updated");

        // 6. SuperAdmin suspends tenant-a -> 200 OK
        var suspendReq = new HttpRequestMessage(HttpMethod.Post, $"/api/platform/tenants/{tenantAId}/suspend");
        suspendReq.Headers.Add("Authorization", $"Bearer {platformAccessToken}");
        var suspendRes = await platformClient.SendAsync(suspendReq);
        suspendRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var suspendedDetail = await suspendRes.Content.ReadFromJsonAsync<TenantDetailResult>();
        suspendedDetail!.Status.Should().Be(TenantStatus.Suspended);

        // 7. Immediately probe tenant-a with valid user token -> 403 TENANT_SUSPENDED (cache eviction proof)
        var probeSuspendedReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        probeSuspendedReq.Headers.Add("Authorization", $"Bearer {tenantAccessToken}");
        var probeSuspendedRes = await tenantClient.SendAsync(probeSuspendedReq);
        probeSuspendedRes.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var suspendedProblem = await probeSuspendedRes.Content.ReadFromJsonAsync<JsonObject>();
        suspendedProblem!["errorCode"]?.ToString().Should().Be("TENANT_SUSPENDED");

        // 8. SuperAdmin reactivates tenant-a -> 200 OK
        var reactivateReq = new HttpRequestMessage(HttpMethod.Post, $"/api/platform/tenants/{tenantAId}/reactivate");
        reactivateReq.Headers.Add("Authorization", $"Bearer {platformAccessToken}");
        var reactivateRes = await platformClient.SendAsync(reactivateReq);
        reactivateRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var reactivatedDetail = await reactivateRes.Content.ReadFromJsonAsync<TenantDetailResult>();
        reactivatedDetail!.Status.Should().Be(TenantStatus.Active);

        // 9. Immediately probe tenant-a again -> 200 OK
        var probeActiveReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        probeActiveReq.Headers.Add("Authorization", $"Bearer {tenantAccessToken}");
        var probeActiveRes = await tenantClient.SendAsync(probeActiveReq);
        probeActiveRes.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
