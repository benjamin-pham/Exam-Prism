namespace Tests.Infrastructure;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Endpoints;
using Common.Localization;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Finbuckle.MultiTenant.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

public sealed class TenantGuardAndBoundaryTests : IAsyncLifetime
{
    private static readonly AsyncLocal<ExamPrismTenantInfo?> CapturedTenantInfoAfterPipeline = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddLocalization(options => options.ResourcesPath = "Common/Localization/Resources");

        var supportedCultures = new[] { "vi-VN", "vi", "en-US", "en" };
        builder.Services.Configure<RequestLocalizationOptions>(options =>
        {
            options.SetDefaultCulture("vi-VN")
                .AddSupportedCultures(supportedCultures)
                .AddSupportedUICultures(supportedCultures);
        });

        builder.Services.Configure<MultiTenancyOptions>(options =>
        {
            options.BaseDomains = ["examprism.com"];
            options.ReservedSubdomains = ["www", "admin", "api"];
            options.ProvisioningRetryAfterSeconds = 5;
            options.AllowHeaderFallbackInDevelopment = false;
        });

        builder.Services.AddMultiTenant<ExamPrismTenantInfo>()
            .WithStrategy<SubdomainMultiTenantStrategy>(ServiceLifetime.Singleton)
            .WithInMemoryStore(options =>
            {
                options.Tenants.Add(new ExamPrismTenantInfo(Guid.NewGuid().ToString(), "active")
                {
                    Name = "Active Org",
                    TenantGuid = Guid.NewGuid(),
                    Status = TenantStatus.Active,
                    ConnectionString = "Host=localhost;Database=active"
                });

                options.Tenants.Add(new ExamPrismTenantInfo(Guid.NewGuid().ToString(), "provisioning")
                {
                    Name = "Provisioning Org",
                    TenantGuid = Guid.NewGuid(),
                    Status = TenantStatus.Provisioning
                });

                options.Tenants.Add(new ExamPrismTenantInfo(Guid.NewGuid().ToString(), "suspended")
                {
                    Name = "Suspended Org",
                    TenantGuid = Guid.NewGuid(),
                    Status = TenantStatus.Suspended
                });

                options.Tenants.Add(new ExamPrismTenantInfo(Guid.NewGuid().ToString(), "failed")
                {
                    Name = "Failed Org",
                    TenantGuid = Guid.NewGuid(),
                    Status = TenantStatus.Failed
                });
            });

        builder.Services.AddScoped<ITenantContext, TenantContext>();
        builder.Services.AddRouting();

        _app = builder.Build();

        _app.UseRequestLocalization();

        // Capture IMultiTenantContextAccessor after TenantAccessGuardMiddleware runs to verify context clearance
        _app.Use(async (context, next) =>
        {
            await next(context);
            var accessor = context.RequestServices.GetService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
            CapturedTenantInfoAfterPipeline.Value = accessor?.MultiTenantContext?.TenantInfo;
        });

        _app.UseMultiTenant();
        _app.UseTenantAccessGuard();

        _app.MapGet("/tenant-only", () => Results.Ok(new { message = "tenant-ok" })).RequireTenant();
        _app.MapGet("/platform-only", () => Results.Ok(new { message = "platform-ok" })).RequirePlatform();
        _app.MapGet("/neutral", () => Results.Ok(new { message = "neutral-ok" }));

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task TenantAccessGuard_RejectsUnknownInvalidProvisioningSuspendedAndFailedTenants_WithLocalizedProblemDetails_AndClearsMultiTenantContext()
    {
        // 1a. unknown.examprism.com with vi-VN -> 404, errorCode == TENANT_NOT_FOUND, localized vi message
        var reqUnknownVi = new HttpRequestMessage(HttpMethod.Get, "http://unknown.examprism.com/neutral");
        reqUnknownVi.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("vi-VN"));
        var resUnknownVi = await _client.SendAsync(reqUnknownVi);

        resUnknownVi.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resUnknownVi.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var problemUnknownVi = await resUnknownVi.Content.ReadFromJsonAsync<JsonElement>();
        problemUnknownVi.GetProperty("status").GetInt32().Should().Be(404);
        problemUnknownVi.GetProperty("errorCode").GetString().Should().Be("TENANT_NOT_FOUND");
        problemUnknownVi.GetProperty("detail").GetString().Should().Be("Không tìm thấy tổ chức theo tên miền này.");
        problemUnknownVi.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();

        // 1b. unknown.examprism.com with en-US -> 404, errorCode == TENANT_NOT_FOUND, localized en message
        var reqUnknownEn = new HttpRequestMessage(HttpMethod.Get, "http://unknown.examprism.com/neutral");
        reqUnknownEn.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var resUnknownEn = await _client.SendAsync(reqUnknownEn);

        resUnknownEn.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problemUnknownEn = await resUnknownEn.Content.ReadFromJsonAsync<JsonElement>();
        problemUnknownEn.GetProperty("status").GetInt32().Should().Be(404);
        problemUnknownEn.GetProperty("errorCode").GetString().Should().Be("TENANT_NOT_FOUND");
        problemUnknownEn.GetProperty("detail").GetString().Should().Be("The requested organization was not found.");

        // 2. a.b.examprism.com (multi-level invalid subdomain) -> 404, errorCode == TENANT_NOT_FOUND
        var reqInvalidHost = new HttpRequestMessage(HttpMethod.Get, "http://a.b.examprism.com/neutral");
        var resInvalidHost = await _client.SendAsync(reqInvalidHost);

        resInvalidHost.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problemInvalidHost = await resInvalidHost.Content.ReadFromJsonAsync<JsonElement>();
        problemInvalidHost.GetProperty("status").GetInt32().Should().Be(404);
        problemInvalidHost.GetProperty("errorCode").GetString().Should().Be("TENANT_NOT_FOUND");

        // 3. provisioning.examprism.com -> 503, Retry-After == 5, errorCode == TENANT_PROVISIONING, clears TenantInfo
        var reqProvisioning = new HttpRequestMessage(HttpMethod.Get, "http://provisioning.examprism.com/neutral");
        reqProvisioning.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var resProvisioning = await _client.SendAsync(reqProvisioning);

        resProvisioning.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        resProvisioning.Headers.TryGetValues("Retry-After", out var retryAfter).Should().BeTrue();
        retryAfter!.Single().Should().Be("5");
        var problemProvisioning = await resProvisioning.Content.ReadFromJsonAsync<JsonElement>();
        problemProvisioning.GetProperty("status").GetInt32().Should().Be(503);
        problemProvisioning.GetProperty("errorCode").GetString().Should().Be("TENANT_PROVISIONING");
        problemProvisioning.GetProperty("detail").GetString().Should().Be("This organization workspace is currently being provisioned. Please try again shortly.");
        CapturedTenantInfoAfterPipeline.Value.Should().BeNull("provisioning tenant context must be cleared via IMultiTenantContextSetter");

        // 4. suspended.examprism.com -> 403, errorCode == TENANT_SUSPENDED, clears TenantInfo
        var reqSuspended = new HttpRequestMessage(HttpMethod.Get, "http://suspended.examprism.com/neutral");
        reqSuspended.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var resSuspended = await _client.SendAsync(reqSuspended);

        resSuspended.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problemSuspended = await resSuspended.Content.ReadFromJsonAsync<JsonElement>();
        problemSuspended.GetProperty("status").GetInt32().Should().Be(403);
        problemSuspended.GetProperty("errorCode").GetString().Should().Be("TENANT_SUSPENDED");
        problemSuspended.GetProperty("detail").GetString().Should().Be("This organization workspace has been suspended.");
        CapturedTenantInfoAfterPipeline.Value.Should().BeNull("suspended tenant context must be cleared via IMultiTenantContextSetter");

        // 5. failed.examprism.com -> 403, errorCode == TENANT_UNAVAILABLE, clears TenantInfo
        var reqFailed = new HttpRequestMessage(HttpMethod.Get, "http://failed.examprism.com/neutral");
        reqFailed.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var resFailed = await _client.SendAsync(reqFailed);

        resFailed.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problemFailed = await resFailed.Content.ReadFromJsonAsync<JsonElement>();
        problemFailed.GetProperty("status").GetInt32().Should().Be(403);
        problemFailed.GetProperty("errorCode").GetString().Should().Be("TENANT_UNAVAILABLE");
        problemFailed.GetProperty("detail").GetString().Should().Be("This organization workspace is currently unavailable.");
        CapturedTenantInfoAfterPipeline.Value.Should().BeNull("failed tenant context must be cleared via IMultiTenantContextSetter");
    }

    [Fact]
    public async Task EndpointBoundaries_EnforceRequireTenantAndRequirePlatform_WhileAllowingNeutralRoutes()
    {
        // 1. examprism.com/tenant-only -> 400, errorCode == TENANT_REQUIRED
        var reqPlatformTenantOnly = new HttpRequestMessage(HttpMethod.Get, "http://examprism.com/tenant-only");
        reqPlatformTenantOnly.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var resPlatformTenantOnly = await _client.SendAsync(reqPlatformTenantOnly);

        resPlatformTenantOnly.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problemTenantOnly = await resPlatformTenantOnly.Content.ReadFromJsonAsync<JsonElement>();
        problemTenantOnly.GetProperty("status").GetInt32().Should().Be(400);
        problemTenantOnly.GetProperty("errorCode").GetString().Should().Be("TENANT_REQUIRED");
        problemTenantOnly.GetProperty("detail").GetString().Should().Be("A valid organization subdomain is required for this endpoint.");

        // 2. examprism.com/platform-only -> 200
        var resPlatformOnly = await _client.GetAsync("http://examprism.com/platform-only");
        resPlatformOnly.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. active.examprism.com/tenant-only -> 200
        var resActiveTenantOnly = await _client.GetAsync("http://active.examprism.com/tenant-only");
        resActiveTenantOnly.StatusCode.Should().Be(HttpStatusCode.OK);

        // 4. active.examprism.com/platform-only -> 404, errorCode == PLATFORM_ONLY
        var reqActivePlatformOnly = new HttpRequestMessage(HttpMethod.Get, "http://active.examprism.com/platform-only");
        reqActivePlatformOnly.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
        var resActivePlatformOnly = await _client.SendAsync(reqActivePlatformOnly);

        resActivePlatformOnly.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problemPlatformOnly = await resActivePlatformOnly.Content.ReadFromJsonAsync<JsonElement>();
        problemPlatformOnly.GetProperty("status").GetInt32().Should().Be(404);
        problemPlatformOnly.GetProperty("errorCode").GetString().Should().Be("PLATFORM_ONLY");
        problemPlatformOnly.GetProperty("detail").GetString().Should().Be("This endpoint is only available on the platform domain.");

        // 5. /neutral -> 200 on both examprism.com and active.examprism.com
        var resPlatformNeutral = await _client.GetAsync("http://examprism.com/neutral");
        resPlatformNeutral.StatusCode.Should().Be(HttpStatusCode.OK);

        var resActiveNeutral = await _client.GetAsync("http://active.examprism.com/neutral");
        resActiveNeutral.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("vi-VN", "TenantNotFound", "Không tìm thấy tổ chức theo tên miền này.")]
    [InlineData("en-US", "TenantNotFound", "The requested organization was not found.")]
    [InlineData("vi-VN", "TenantProvisioning", "Không gian làm việc của tổ chức đang được khởi tạo. Vui lòng thử lại sau giây lát.")]
    [InlineData("en-US", "TenantProvisioning", "This organization workspace is currently being provisioned. Please try again shortly.")]
    [InlineData("vi-VN", "TenantSuspended", "Không gian làm việc của tổ chức đã bị tạm khóa.")]
    [InlineData("en-US", "TenantSuspended", "This organization workspace has been suspended.")]
    [InlineData("vi-VN", "TenantUnavailable", "Không gian làm việc của tổ chức hiện không khả dụng.")]
    [InlineData("en-US", "TenantUnavailable", "This organization workspace is currently unavailable.")]
    [InlineData("vi-VN", "TenantRequired", "Yêu cầu truy cập qua tên miền phụ (subdomain) của tổ chức.")]
    [InlineData("en-US", "TenantRequired", "A valid organization subdomain is required for this endpoint.")]
    [InlineData("vi-VN", "PlatformOnly", "Đường dẫn này chỉ khả dụng trên tên miền hệ thống.")]
    [InlineData("en-US", "PlatformOnly", "This endpoint is only available on the platform domain.")]
    [InlineData("vi-VN", "TenantTokenMismatch", "Mã xác thực không thuộc về tổ chức này.")]
    [InlineData("en-US", "TenantTokenMismatch", "The authentication token does not belong to this organization.")]
    public void StringLocalizer_ResolvesAllSevenMultiTenancyKeys_ForVietnameseAndEnglish(
        string cultureName,
        string resourceKey,
        string expectedText)
    {
        using var scope = _app.Services.CreateScope();
        var localizer = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<SharedResources>>();

        var culture = new CultureInfo(cultureName);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        var localized = localizer[resourceKey];
        localized.ResourceNotFound.Should().BeFalse($"key '{resourceKey}' should be found in '{cultureName}'");
        localized.Value.Should().Be(expectedText);
    }

    [Fact]
    public void EndpointBoundaries_AttachTenantBoundaryMetadata_ToEndpointMetadata()
    {
        var endpointDataSources = _app.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var allEndpoints = endpointDataSources.SelectMany(ds => ds.Endpoints).ToList();

        var tenantEndpoint = allEndpoints.First(e => (e as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText == "/tenant-only");
        var platformEndpoint = allEndpoints.First(e => (e as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText == "/platform-only");
        var neutralEndpoint = allEndpoints.First(e => (e as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText == "/neutral");

        tenantEndpoint.Metadata.GetMetadata<TenantBoundaryMetadata>()
            .Should().NotBeNull()
            .And.Be(new TenantBoundaryMetadata(TenantBoundaryMode.TenantRequired));

        platformEndpoint.Metadata.GetMetadata<TenantBoundaryMetadata>()
            .Should().NotBeNull()
            .And.Be(new TenantBoundaryMetadata(TenantBoundaryMode.PlatformOnly));

        neutralEndpoint.Metadata.GetMetadata<TenantBoundaryMetadata>()
            .Should().BeNull();
    }
}
