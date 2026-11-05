namespace Tests.Integration;

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Common.MultiTenancy;
using ExamPrism.Features.Auth;
using ExamPrism.Features.Platform.Auth;
using ExamPrism.Features.Tenants;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Tests.Infrastructure;
using Xunit;

public class TenantProvisioningAndFleetMigrationIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public TenantProvisioningAndFleetMigrationIntegrationTests(CustomWebApplicationFactory factory)
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
    public async Task CheckSlug_PublicEndpoint_ReturnsAvailableForNewSlug_AndUnavailableForReservedOrTakenSlugs()
    {
        var platformClient = _factory.CreatePlatformClient();

        // Ensure sunrise-academy is not in DB if a previous test created it
        using (var scope = _factory.Services.CreateScope())
        {
            var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var existing = await catalogDb.Tenants.FirstOrDefaultAsync(t => t.Slug == "sunrise-academy");
            if (existing is not null)
            {
                catalogDb.Tenants.Remove(existing);
                await catalogDb.SaveChangesAsync();
                var cacheInvalidator = scope.ServiceProvider.GetRequiredService<ITenantCacheInvalidator>();
                await cacheInvalidator.InvalidateTenantAsync(existing.Id, existing.Slug);
            }
        }

        // 1. Available new slug: sunrise-academy
        var availRes = await platformClient.GetAsync("/api/tenants/check-slug/sunrise-academy");
        availRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var avail = await availRes.Content.ReadFromJsonAsync<CheckTenantSlugResult>();
        avail.Should().NotBeNull();
        avail!.Slug.Should().Be("sunrise-academy");
        avail.IsAvailable.Should().BeTrue();
        avail.ReasonCode.Should().BeNull();

        // 2. Reserved slug: admin
        var reservedRes = await platformClient.GetAsync("/api/tenants/check-slug/admin");
        reservedRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var reserved = await reservedRes.Content.ReadFromJsonAsync<CheckTenantSlugResult>();
        reserved.Should().NotBeNull();
        reserved!.Slug.Should().Be("admin");
        reserved.IsAvailable.Should().BeFalse();
        reserved.ReasonCode.Should().Be("RESERVED_SLUG");

        // 3. Taken slug: tenant-a
        var takenRes = await platformClient.GetAsync("/api/tenants/check-slug/tenant-a");
        takenRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var taken = await takenRes.Content.ReadFromJsonAsync<CheckTenantSlugResult>();
        taken.Should().NotBeNull();
        taken!.Slug.Should().Be("tenant-a");
        taken.IsAvailable.Should().BeFalse();
        taken.ReasonCode.Should().Be("SLUG_ALREADY_TAKEN");
    }

    [Fact]
    public async Task SelfServiceOnboarding_CreatesPhysicalDatabase_MigratesSchema_SeedsTenantOwner_WipesOwnerPassword_AndAllowsImmediateTenantLogin()
    {
        // 1. Probe sunrise-academy before onboarding -> 404 TENANT_NOT_FOUND (populates negative cache)
        var tenantClient = _factory.CreateTenantClient("sunrise-academy");
        var probeRes = await tenantClient.GetAsync("/api/auth/me");
        probeRes.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var probeProblem = await probeRes.Content.ReadFromJsonAsync<JsonObject>();
        probeProblem!["errorCode"]?.ToString().Should().Be("TENANT_NOT_FOUND");

        // 2. Self-service onboarding: POST /api/tenants on platform host -> 202 Accepted
        var platformClient = _factory.CreatePlatformClient();
        var onboardCmd = new CreateTenantCommand(
            "sunrise-academy",
            "Sunrise Academy",
            "owner@sunrise.edu",
            "OwnerPassword123!");

        var onboardRes = await platformClient.PostAsJsonAsync("/api/tenants", onboardCmd);
        onboardRes.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // 3. Poll status until Provisioned
        var sw = Stopwatch.StartNew();
        GetTenantProvisioningStatusResult? statusResult = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(25))
        {
            var statusRes = await platformClient.GetAsync("/api/tenants/sunrise-academy/status");
            if (statusRes.IsSuccessStatusCode)
            {
                statusResult = await statusRes.Content.ReadFromJsonAsync<GetTenantProvisioningStatusResult>();
                if (statusResult?.Status == TenantStatus.Active && statusResult.ProvisionedAt is not null)
                {
                    break;
                }
            }
            await Task.Delay(250);
        }

        statusResult.Should().NotBeNull("Provisioning should complete within timeout");
        statusResult!.Status.Should().Be(TenantStatus.Active);
        statusResult.ProvisionedAt.Should().NotBeNull();

        // 4. Assert CatalogDbContext has encrypted connection string and EncryptedOwnerPassword wiped
        using (var scope = _factory.Services.CreateScope())
        {
            var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var tenantRecord = await catalogDb.Tenants.AsNoTracking().SingleAsync(t => t.Slug == "sunrise-academy");
            tenantRecord.EncryptedConnectionString.Should().NotBeNullOrWhiteSpace();
            tenantRecord.EncryptedOwnerPassword.Should().BeNull();
        }

        // 5. Login on tenant host as newly seeded owner -> 200 OK + /api/auth/me returns owner email
        var loginRes = await tenantClient.PostAsJsonAsync("/api/auth/login",
            new LoginCommand("owner@sunrise.edu", "OwnerPassword123!"));
        loginRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var (accessCookie, _) = ExtractCookies(loginRes);
        accessCookie.Should().NotBeNullOrWhiteSpace();

        var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        meReq.Headers.Add("Cookie", $"access_token={accessCookie}");
        var meRes = await tenantClient.SendAsync(meReq);
        meRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var me = await meRes.Content.ReadFromJsonAsync<GetMeResult>();
        me.Should().NotBeNull();
        me!.Email.Should().Be("owner@sunrise.edu");
    }

    [Fact]
    public async Task DedicatedPostgresUserProvisioning_CreatesRoleAndDatabaseOwnedByRole_AndAppliesMigrations()
    {
        using var scope = _factory.Services.CreateScope();
        var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var protector = scope.ServiceProvider.GetRequiredService<IConnectionStringProtector>();
        var normalizer = scope.ServiceProvider.GetRequiredService<ITenantConnectionStringNormalizer>();
        var cacheInvalidator = scope.ServiceProvider.GetRequiredService<ITenantCacheInvalidator>();
        var scopeFactory = scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<TenantProvisioner>>();

        var baseOptions = scope.ServiceProvider.GetRequiredService<IOptions<MultiTenancyOptions>>().Value;
        var customOptions = new MultiTenancyOptions
        {
            BaseDomains = baseOptions.BaseDomains,
            ReservedSubdomains = baseOptions.ReservedSubdomains,
            AllowHeaderFallbackInDevelopment = baseOptions.AllowHeaderFallbackInDevelopment,
            HeaderName = baseOptions.HeaderName,
            Provisioning = new TenantProvisioningOptions
            {
                CreateDedicatedPostgresUser = true,
                ServerConnectionString = baseOptions.Provisioning.ServerConnectionString,
                DedicatedUserPrefix = baseOptions.Provisioning.DedicatedUserPrefix,
                DatabaseNamePrefix = baseOptions.Provisioning.DatabaseNamePrefix,
                RecoverStuckProvisioningOnStartup = baseOptions.Provisioning.RecoverStuckProvisioningOnStartup
            },
            FleetMigration = baseOptions.FleetMigration
        };

        var provisioner = new TenantProvisioner(
            catalogDb,
            configuration,
            Options.Create(customOptions),
            protector,
            normalizer,
            cacheInvalidator,
            scopeFactory,
            timeProvider,
            logger);

        // Ensure isolated-org is clean
        var existing = await catalogDb.Tenants.FirstOrDefaultAsync(t => t.Slug == "isolated-org");
        if (existing is not null)
        {
            catalogDb.Tenants.Remove(existing);
            await catalogDb.SaveChangesAsync();
            await cacheInvalidator.InvalidateTenantAsync(existing.Id, existing.Slug);
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = "isolated-org",
            Name = "Isolated Org",
            Status = TenantStatus.Provisioning,
            OwnerEmail = "owner@isolated-org.edu",
            EncryptedOwnerPassword = protector.Protect("OwnerPassword123!"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        catalogDb.Tenants.Add(tenant);
        await catalogDb.SaveChangesAsync();

        // Provision
        await provisioner.ProvisionAsync(tenant.Id);

        // Verify status and secrets
        var updatedTenant = await catalogDb.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id);
        updatedTenant.Status.Should().Be(TenantStatus.Active);
        updatedTenant.EncryptedConnectionString.Should().NotBeNullOrWhiteSpace();
        updatedTenant.EncryptedOwnerPassword.Should().BeNull();

        // Verify role and database ownership in PostgreSQL
        var serverConnStr = catalogDb.Database.GetConnectionString()!;
        var adminConnStr = new NpgsqlConnectionStringBuilder(serverConnStr)
        {
            Database = "postgres"
        }.ConnectionString;

        await using (var adminConn = new NpgsqlConnection(adminConnStr))
        {
            await adminConn.OpenAsync();

            await using var roleCmd = adminConn.CreateCommand();
            roleCmd.CommandText = "SELECT rolname FROM pg_roles WHERE rolname = 'examprism_u_isolated_org';";
            var role = await roleCmd.ExecuteScalarAsync();
            role.Should().Be("examprism_u_isolated_org");

            await using var ownerCmd = adminConn.CreateCommand();
            ownerCmd.CommandText = "SELECT r.rolname FROM pg_database d JOIN pg_roles r ON d.datdba = r.oid WHERE d.datname = 'examprism_tenant_isolated_org';";
            var dbOwner = await ownerCmd.ExecuteScalarAsync();
            dbOwner.Should().Be("examprism_u_isolated_org");
        }

        // Verify seeded owner exists in tenant database via dedicated user connection string
        var tenantConnStr = protector.Unprotect(updatedTenant.EncryptedConnectionString!);
        await using (var tenantConn = new NpgsqlConnection(tenantConnStr))
        {
            await tenantConn.OpenAsync();

            await using var userCmd = tenantConn.CreateCommand();
            userCmd.CommandText = "SELECT \"Email\" FROM \"AspNetUsers\" WHERE \"Email\" = 'owner@isolated-org.edu';";
            var ownerEmail = await userCmd.ExecuteScalarAsync();
            ownerEmail.Should().Be("owner@isolated-org.edu");
        }
    }

    [Fact]
    public async Task AdminRetryProvisioning_RecoversFailedTenant_ToActiveStatus()
    {
        var failedTenantId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        // Update failed-org with owner credentials
        using (var scope = _factory.Services.CreateScope())
        {
            var catalogDb = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var protector = scope.ServiceProvider.GetRequiredService<IConnectionStringProtector>();
            var failedTenant = await catalogDb.Tenants.SingleAsync(t => t.Id == failedTenantId);
            failedTenant.OwnerEmail = "owner@failed-org.edu";
            failedTenant.EncryptedOwnerPassword = protector.Protect("OwnerPassword123!");
            await catalogDb.SaveChangesAsync();
        }

        // SuperAdmin login on platform host
        var platformClient = _factory.CreatePlatformClient();
        var loginRes = await platformClient.PostAsJsonAsync("/api/platform/auth/login",
            new PlatformLoginCommand("admin@examprism.com", "AdminPassword123!"));
        loginRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var (adminToken, _) = ExtractCookies(loginRes);
        adminToken.Should().NotBeNullOrWhiteSpace();

        // Retry provisioning via Admin API -> 202 Accepted
        var retryReq = new HttpRequestMessage(HttpMethod.Post, $"/api/platform/tenants/{failedTenantId}/retry-provisioning");
        retryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var retryRes = await platformClient.SendAsync(retryReq);
        retryRes.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // Poll status until Active
        var sw = Stopwatch.StartNew();
        GetTenantProvisioningStatusResult? statusResult = null;
        while (sw.Elapsed < TimeSpan.FromSeconds(25))
        {
            var statusRes = await platformClient.GetAsync("/api/tenants/failed-org/status");
            if (statusRes.IsSuccessStatusCode)
            {
                statusResult = await statusRes.Content.ReadFromJsonAsync<GetTenantProvisioningStatusResult>();
                if (statusResult?.Status == TenantStatus.Active && statusResult.ProvisionedAt is not null)
                {
                    break;
                }
            }
            await Task.Delay(250);
        }

        statusResult.Should().NotBeNull("Failed tenant should be reprovisioned to Active");
        statusResult!.Status.Should().Be(TenantStatus.Active);
        statusResult.ProvisionedAt.Should().NotBeNull();

        // Tenant login as owner -> 200 OK
        var failedOrgClient = _factory.CreateTenantClient("failed-org");
        var ownerLoginRes = await failedOrgClient.PostAsJsonAsync("/api/auth/login",
            new LoginCommand("owner@failed-org.edu", "OwnerPassword123!"));
        ownerLoginRes.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FleetMigrationEndpoints_MigrateAllAndSingleActiveTenants_AsSuperAdmin()
    {
        var platformClient = _factory.CreatePlatformClient();

        // 1. Unauthorized request without token -> 401 Unauthorized
        var unauthReq = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants/migrate");
        var unauthRes = await platformClient.SendAsync(unauthReq);
        unauthRes.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 2. SuperAdmin login
        var loginRes = await platformClient.PostAsJsonAsync("/api/platform/auth/login",
            new PlatformLoginCommand("admin@examprism.com", "AdminPassword123!"));
        loginRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var (adminToken, _) = ExtractCookies(loginRes);
        adminToken.Should().NotBeNullOrWhiteSpace();

        // 3. POST /api/platform/tenants/migrate -> 200 OK
        var migrateAllReq = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants/migrate");
        migrateAllReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var migrateAllRes = await platformClient.SendAsync(migrateAllReq);
        migrateAllRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await migrateAllRes.Content.ReadFromJsonAsync<FleetMigrationSummaryResult>();
        summary.Should().NotBeNull();
        summary!.FailedCount.Should().Be(0);
        summary.SucceededCount.Should().BeGreaterThanOrEqualTo(2);
        summary.TotalTenants.Should().Be(summary.SucceededCount);

        // 4. POST /api/platform/tenants/11111111-1111-1111-1111-111111111111/migrate -> 200 OK
        var tenantAId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var migrateSingleReq = new HttpRequestMessage(HttpMethod.Post, $"/api/platform/tenants/{tenantAId}/migrate");
        migrateSingleReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var migrateSingleRes = await platformClient.SendAsync(migrateSingleReq);
        migrateSingleRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var item = await migrateSingleRes.Content.ReadFromJsonAsync<TenantMigrationItemResult>();
        item.Should().NotBeNull();
        item!.Succeeded.Should().BeTrue();
        item.Slug.Should().Be("tenant-a");
        item.TenantId.Should().Be(tenantAId);
    }
}
