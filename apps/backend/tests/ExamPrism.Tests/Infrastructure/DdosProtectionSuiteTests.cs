namespace ExamPrism.Tests.Infrastructure;

using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Text;
using Common.MultiTenancy;
using Common.Security;
using Common.Telemetry;
using ExamPrism.Infrastructure.Http;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using ExamPrism.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Xunit;

public class DdosProtectionSuiteTests
{
    [Fact]
    public void ReverseProxySetup_IgnoresSpoofedHeadersFromUntrustedPublicIp_AndHonorsTrustedProxyWithValidatedIp()
    {
        ExamPrismDiagnostics.ResetForTesting();

        // 1. Untrusted public client trying to spoof headers
        var ctxUntrusted = new DefaultHttpContext();
        ctxUntrusted.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.77");
        ctxUntrusted.Request.Headers["CF-Connecting-IP"] = "203.0.113.5";
        ctxUntrusted.Request.Headers["X-Real-IP"] = "203.0.113.6";
        ctxUntrusted.Request.Headers["X-Forwarded-For"] = "203.0.113.7";

        var resolvedUntrusted = ReverseProxySetup.ResolveClientIp(ctxUntrusted);
        resolvedUntrusted.Should().Be("198.51.100.77");
        ExamPrismDiagnostics.SpoofedIpIgnoredCount.Should().Be(1);

        // 2. Trusted proxy peer (RFC 1918) forwarding client IP, skipping invalid candidates
        var ctxTrusted = new DefaultHttpContext();
        ctxTrusted.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.10");
        ctxTrusted.Request.Headers["CF-Connecting-IP"] = "not-a-valid-ip";
        ctxTrusted.Request.Headers["X-Real-IP"] = "203.0.113.99";

        var resolvedTrusted = ReverseProxySetup.ResolveClientIp(ctxTrusted);
        resolvedTrusted.Should().Be("203.0.113.99");
    }

    [Fact]
    public void RuntimeTuningSetup_ConfiguresKestrelAntiSlowlorisDataRates_AndHeaderLimits()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["RuntimeTuning:MinRequestBodyDataRateBytesPerSecond"] = "500";
        builder.Configuration["RuntimeTuning:MinResponseDataRateBytesPerSecond"] = "600";
        builder.Configuration["RuntimeTuning:DataRateGracePeriodSeconds"] = "7";
        builder.Configuration["RuntimeTuning:MaxRequestHeaderCount"] = "64";
        builder.Configuration["RuntimeTuning:MaxRequestHeadersTotalSizeBytes"] = "16384";
        builder.Configuration["RuntimeTuning:MaxRequestLineSizeBytes"] = "4096";

        builder.ConfigureAppKestrel();

        using var app = builder.Build();
        var kestrelOptions = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;

        kestrelOptions.Limits.MaxRequestHeaderCount.Should().Be(64);
        kestrelOptions.Limits.MaxRequestHeadersTotalSize.Should().Be(16384);
        kestrelOptions.Limits.MaxRequestLineSize.Should().Be(4096);
        kestrelOptions.Limits.MinRequestBodyDataRate.Should().NotBeNull();
        kestrelOptions.Limits.MinRequestBodyDataRate!.BytesPerSecond.Should().Be(500);
        kestrelOptions.Limits.MinRequestBodyDataRate!.GracePeriod.Should().Be(TimeSpan.FromSeconds(7));
        kestrelOptions.Limits.MinResponseDataRate.Should().NotBeNull();
        kestrelOptions.Limits.MinResponseDataRate!.BytesPerSecond.Should().Be(600);
    }

    [Fact]
    public async Task HybridIpReputationService_And_IpPenaltyBoxMiddleware_BansAbusiveIp_WhileAllowingValidJwtOnSharedSchoolNatIp()
    {
        ExamPrismDiagnostics.ResetForTesting();
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var ddosOptions = Options.Create(new DdosProtectionOptions
        {
            Enabled = true,
            BanThresholdScore = 40,
            UnknownTenantProbeWeight = 20,
            BanDurationSeconds = 120,
            BypassPenaltyBoxForAuthenticatedJwt = true
        });

        var reputationService = new HybridIpReputationService(ddosOptions, timeProvider: fakeTime);

        const string schoolNatIp = "203.0.113.50";

        // 1. Record 2 strikes for the IP
        await reputationService.RecordStrikeAsync(schoolNatIp, IpStrikeReason.UnknownTenantProbe);
        await reputationService.RecordStrikeAsync(schoolNatIp, IpStrikeReason.UnknownTenantProbe);

        var banRemaining = await reputationService.GetBanRemainingAsync(schoolNatIp);
        banRemaining.Should().NotBeNull();
        banRemaining!.Value.TotalSeconds.Should().Be(120);
        ExamPrismDiagnostics.IpBansIssuedCount.Should().Be(1);

        // Setup middleware
        var jwtOptions = Options.Create(new JwtOptions
        {
            Issuer = "test-issuer",
            Audience = "test-aud",
            SecretKey = "super-secret-key-that-is-at-least-32-bytes-long!"
        });

        var reverseProxyOptions = Options.Create(new ReverseProxyOptions());

        var nextCalled = false;
        RequestDelegate next = ctx =>
        {
            nextCalled = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };

        var middleware = new IpPenaltyBoxMiddleware(next, reputationService, ddosOptions, jwtOptions, reverseProxyOptions);

        // 2. Unauthenticated request from banned IP -> 429 Too Many Requests
        var unauthCtx = new DefaultHttpContext();
        unauthCtx.Connection.RemoteIpAddress = IPAddress.Parse(schoolNatIp);
        unauthCtx.Request.Path = "/api/any-endpoint";

        nextCalled = false;
        await middleware.InvokeAsync(unauthCtx);

        nextCalled.Should().BeFalse();
        unauthCtx.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        unauthCtx.Response.Headers["X-Penalty-Box"].ToString().Should().Be("true");
        unauthCtx.Response.Headers["Retry-After"].ToString().Should().Be("120");
        ExamPrismDiagnostics.IpPenaltyBoxRejectedCount.Should().Be(1);

        // 3. Valid JWT on the same banned IP -> Bypasses Penalty Box (School NAT safety!)
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Value.SecretKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: jwtOptions.Value.Issuer,
            audience: jwtOptions.Value.Audience,
            claims: [new Claim(ClaimTypes.NameIdentifier, "student-123")],
            expires: fakeTime.GetUtcNow().UtcDateTime.AddMinutes(15),
            signingCredentials: creds);
        var validJwt = new JwtSecurityTokenHandler().WriteToken(token);

        var validJwtCtx = new DefaultHttpContext();
        validJwtCtx.Connection.RemoteIpAddress = IPAddress.Parse(schoolNatIp);
        validJwtCtx.Request.Path = "/api/exams/active/submit";
        validJwtCtx.Request.Headers["Authorization"] = $"Bearer {validJwt}";

        nextCalled = false;
        await middleware.InvokeAsync(validJwtCtx);

        nextCalled.Should().BeTrue();
        validJwtCtx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        // 4. Invalid/tampered JWT on the same banned IP -> Blocked with 429
        var invalidJwtCtx = new DefaultHttpContext();
        invalidJwtCtx.Connection.RemoteIpAddress = IPAddress.Parse(schoolNatIp);
        invalidJwtCtx.Request.Path = "/api/exams/active/submit";
        invalidJwtCtx.Request.Headers["Authorization"] = "Bearer tampered.token.here";

        nextCalled = false;
        await middleware.InvokeAsync(invalidJwtCtx);

        nextCalled.Should().BeFalse();
        invalidJwtCtx.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task DecompressionBombGuardMiddleware_AbortsStreamExceedingMaxDecompressedSize_Returns413AndBansClientIp()
    {
        ExamPrismDiagnostics.ResetForTesting();

        var ddosOptions = Options.Create(new DdosProtectionOptions
        {
            Enabled = true,
            BanThresholdScore = 100,
            DecompressionBombWeight = 100,
            BanDurationSeconds = 300
        });

        var reputationService = new HybridIpReputationService(ddosOptions);
        var runtimeOptions = Options.Create(new RuntimeTuningOptions
        {
            MaxDecompressedBodySizeBytes = 1024 // 1 KB cap
        });
        var reverseProxyOptions = Options.Create(new ReverseProxyOptions());

        const string attackerIp = "198.51.100.88";

        RequestDelegate next = async ctx =>
        {
            // Drain request body
            var buffer = new byte[512];
            while (await ctx.Request.Body.ReadAsync(buffer, 0, buffer.Length) > 0)
            {
            }
            ctx.Response.StatusCode = StatusCodes.Status200OK;
        };

        var middleware = new DecompressionBombGuardMiddleware(next, runtimeOptions, reputationService, reverseProxyOptions);

        var payload4Kb = new byte[4096];
        Random.Shared.NextBytes(payload4Kb);

        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(attackerIp);
        ctx.Request.Body = new MemoryStream(payload4Kb);
        ctx.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(ctx);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        ExamPrismDiagnostics.DecompressionBombRejectedCount.Should().Be(1);

        var isBanned = await reputationService.GetBanRemainingAsync(attackerIp);
        isBanned.Should().NotBeNull();
        isBanned!.Value.TotalSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CatalogMultiTenantStore_RejectsInvalidSlugWithoutCacheOrDb_KeepsNegativeCacheOutOfDistributedCache_AndEnforcesDbMissBudget()
    {
        ExamPrismDiagnostics.ResetForTesting();

        var recordingCache = new RecordingDistributedCache();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IDistributedCache>(recordingCache);
        services.AddHybridCache();
        var sp = services.BuildServiceProvider();
        var hybridCache = sp.GetRequiredService<HybridCache>();

        var protector = Substitute.For<IConnectionStringProtector>();
        var interceptor = new EmptyQueryCountingInterceptor();
        var connInterceptor = new SuppressConnectionInterceptor();

        var dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=dummy_test;Username=dummy;Password=dummy")
            .AddInterceptors(connInterceptor, interceptor)
            .Options;

        await using var db = new CatalogDbContext(dbOptions);

        var mtOptions = Options.Create(new MultiTenancyOptions
        {
            CatalogCacheExpirationMinutes = 30,
            CatalogLocalCacheExpirationMinutes = 5,
            NotFoundCacheExpirationSeconds = 30,
            MaxNegativeCacheEntries = 100,
            MaxCatalogDbMissesPerSecond = 5
        });

        var store = new CatalogMultiTenantStore(db, hybridCache, protector, mtOptions);

        // 1. Invalid slug format rejects immediately with 0 DB queries and 0 cache writes
        var resInvalid1 = await store.GetByIdentifierAsync("INVALID_SLUG_$$$");
        var resInvalid2 = await store.GetByIdentifierAsync("-bad-slug-");
        resInvalid1.Should().BeNull();
        resInvalid2.Should().BeNull();
        interceptor.ExecutedSelectCount.Should().Be(0);
        recordingCache.Entries.Should().BeEmpty();

        // 2. Cold miss on valid slug queries DB once, caches in L1, and keeps negative cache out of Redis L2
        var resMissing1 = await store.GetByIdentifierAsync("missing-tenant-1");
        resMissing1.Should().BeNull();
        interceptor.ExecutedSelectCount.Should().Be(1);

        var resMissing2 = await store.GetByIdentifierAsync("missing-tenant-1");
        resMissing2.Should().BeNull();
        interceptor.ExecutedSelectCount.Should().Be(1); // Cached in L1, no second DB query

        recordingCache.Entries.Should().BeEmpty("negative cache must not pollute distributed cache (L2)");

        // 3. Flood of 20 distinct cold misses in the same second is throttled by MaxCatalogDbMissesPerSecond (5)
        for (var i = 0; i < 20; i++)
        {
            await store.GetByIdentifierAsync($"random-slug-{i}");
        }

        // Initial 1 query from missing-tenant-1 + at most 5 permitted misses = at most 6 queries total
        interceptor.ExecutedSelectCount.Should().BeInRange(1, 6);
        ExamPrismDiagnostics.TenantCatalogShieldRejectedCount.Should().BeGreaterThanOrEqualTo(14);
    }

    [Fact]
    public async Task EndToEndPipeline_ExposesDdosPrometheusMetrics_EnforcesTenantOnboardingPolicy_AndEscalatesRepeatedViolationsToPenaltyBox()
    {
        ExamPrismDiagnostics.ResetForTesting();

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:CatalogConnection", "Host=127.0.0.1;Port=5432;Database=examprism_dummy;Username=postgres;Password=postgres");
            builder.UseSetting("ConnectionStrings:Redis", "");
            builder.UseSetting("Jwt:Issuer", "ExamPrism");
            builder.UseSetting("Jwt:Audience", "ExamPrism");
            builder.UseSetting("Jwt:SecretKey", "SuperSecretKeyForDevJwtTokensMin256BitsLongExamPrismDevOnly!");
            builder.UseSetting("PlatformAdmin:SeedOnStartup", "false");
            builder.UseSetting("MultiTenancy:Provisioning:RecoverStuckProvisioningOnStartup", "false");
            builder.UseSetting("MultiTenancy:FleetMigration:MigrateOnStartup", "false");
            builder.UseSetting("Orleans:SiloPort", "0");
            builder.UseSetting("Orleans:GatewayPort", "0");
            builder.UseSetting("Persistence:MaxRetryCount", "0");
            builder.UseSetting("DdosProtection:Enabled", "true");
            builder.UseSetting("DdosProtection:BanThresholdScore", "30");
            builder.UseSetting("DdosProtection:UnknownTenantProbeWeight", "15");
            builder.UseSetting("RateLimiting:TenantOnboarding:PermitLimit", "2");
            builder.UseSetting("RateLimiting:TenantOnboarding:WindowSeconds", "60");

            builder.ConfigureServices(services =>
            {
                var fakeDbInterceptor = new EmptyQueryCountingInterceptor();
                var dbOptions = new DbContextOptionsBuilder<CatalogDbContext>()
                    .UseNpgsql("Host=127.0.0.1;Database=fake")
                    .AddInterceptors(fakeDbInterceptor, new SuppressConnectionInterceptor())
                    .Options;

                services.RemoveAll<DbContextOptions<CatalogDbContext>>();
                services.AddSingleton(dbOptions);
                services.RemoveAll<CatalogDbContext>();
                services.AddScoped(_ => new CatalogDbContext(dbOptions));
            });
        });

        using var client = factory.CreateClient();

        // 1. Send 2 requests with non-existent tenant candidate from trusted proxy (RFC 1918) forwarding 203.0.113.200
        using var req1 = new HttpRequestMessage(HttpMethod.Get, "/api/test/ddos-probe");
        req1.Headers.Add("X-Tenant", "unknown-probe-1");
        req1.Headers.Add("X-Forwarded-For", "203.0.113.200");
        var res1 = await client.SendAsync(req1);
        res1.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/test/ddos-probe");
        req2.Headers.Add("X-Tenant", "unknown-probe-2");
        req2.Headers.Add("X-Forwarded-For", "203.0.113.200");
        var res2 = await client.SendAsync(req2);
        res2.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // 2. 3rd request from 203.0.113.200 is blocked early by IpPenaltyBoxMiddleware
        using var req3 = new HttpRequestMessage(HttpMethod.Get, "/api/test/ddos-probe");
        req3.Headers.Add("X-Forwarded-For", "203.0.113.200");
        var res3 = await client.SendAsync(req3);
        res3.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        res3.Headers.Contains("X-Penalty-Box").Should().BeTrue();
        res3.Headers.GetValues("X-Penalty-Box").First().Should().Be("true");

        // 3. Rate limiting on Tenant Onboarding: 3rd request rejected
        for (var i = 1; i <= 3; i++)
        {
            using var onboardReq = new HttpRequestMessage(HttpMethod.Post, "/api/tenants");
            onboardReq.Headers.Add("X-Forwarded-For", "203.0.113.205");
            onboardReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            var onboardRes = await client.SendAsync(onboardReq);
            if (i <= 2)
            {
                onboardRes.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
            }
            else
            {
                onboardRes.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            }
        }

        // 4. Prometheus /metrics exposes all 6 DDoS metrics
        var metricsRes = await client.GetAsync("/metrics");
        metricsRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var metricsBody = await metricsRes.Content.ReadAsStringAsync();
        metricsBody.Should().Contain("examprism_security_ip_penalty_box_rejected_total");
        metricsBody.Should().Contain("examprism_security_ip_bans_issued_total");
        metricsBody.Should().Contain("examprism_security_decompression_bomb_rejected_total");
        metricsBody.Should().Contain("examprism_security_tenant_catalog_shield_rejected_total");
        metricsBody.Should().Contain("examprism_security_spoofed_ip_ignored_total");
        metricsBody.Should().Contain("examprism_http_rate_limit_rejected_total");
    }

    private sealed class EmptyQueryCountingInterceptor : DbCommandInterceptor
    {
        private int _executedSelectCount;
        public int ExecutedSelectCount => _executedSelectCount;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _executedSelectCount);
            }

            var table = new DataTable();
            table.Columns.Add("Id", typeof(Guid));
            table.Columns.Add("CreatedAt", typeof(DateTime));
            table.Columns.Add("EncryptedConnectionString", typeof(string));
            table.Columns.Add("EncryptedOwnerPassword", typeof(string));
            table.Columns.Add("FailureReason", typeof(string));
            table.Columns.Add("Name", typeof(string));
            table.Columns.Add("OwnerEmail", typeof(string));
            table.Columns.Add("ProvisionedAt", typeof(DateTime));
            table.Columns.Add("Slug", typeof(string));
            table.Columns.Add("Status", typeof(string));
            table.Columns.Add("UpdatedAt", typeof(DateTime));

            return new ValueTask<InterceptionResult<DbDataReader>>(
                InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader()));
        }
    }

    private sealed class SuppressConnectionInterceptor : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<InterceptionResult>(InterceptionResult.Suppress());
        }
    }

    private sealed class RecordingDistributedCache : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
        public ConcurrentDictionary<string, byte[]> Entries { get; } = new();

        public byte[]? Get(string key) => _inner.Get(key);

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            Entries[key] = value;
            _inner.Set(key, value, options);
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Entries[key] = value;
            return _inner.SetAsync(key, value, options, token);
        }

        public void Refresh(string key) => _inner.Refresh(key);

        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);

        public void Remove(string key)
        {
            Entries.TryRemove(key, out _);
            _inner.Remove(key);
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Entries.TryRemove(key, out _);
            return _inner.RemoveAsync(key, token);
        }
    }
}

