namespace ExamPrism.Tests.Infrastructure;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Common.Caching;
using Common.Exceptions;
using Common.MultiTenancy;
using Common.Security;
using Common.Telemetry;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Telemetry;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenTelemetry.Trace;
using Xunit;

public class IdentityHighLoadAndTelemetryTests
{
    [Fact]
    public async Task BulkheadPasswordHasher_HashesAndVerifiesNormally_AndThrowsBulkheadRejectedWhenConcurrencyExhausted()
    {
        var initialRejected = ExamPrismDiagnostics.PasswordHashBulkheadRejectedCount;

        var options = Options.Create(new IdentityHighLoadOptions
        {
            MaxConcurrentPasswordHashes = 1,
            PasswordHashQueueTimeoutMs = 50
        });

        var blocker = new ManualResetEventSlim(false);
        var innerHasher = Substitute.For<IPasswordHasher<ApplicationUser>>();
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = "testuser" };

        innerHasher.HashPassword(Arg.Any<ApplicationUser>(), Arg.Any<string>())
            .Returns(callInfo =>
            {
                var password = callInfo.Arg<string>();
                if (password == "slow-password")
                {
                    blocker.Wait(TimeSpan.FromSeconds(5));
                }
                return "hashed_" + password;
            });

        innerHasher.VerifyHashedPassword(Arg.Any<ApplicationUser>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(PasswordVerificationResult.Success);

        var hasher = new BulkheadPasswordHasher<ApplicationUser>(options, innerHasher);

        // 1. Normal hashing and verification works
        var hash = hasher.HashPassword(user, "normal-password");
        hash.Should().Be("hashed_normal-password");

        var verifyResult = hasher.VerifyHashedPassword(user, hash, "normal-password");
        verifyResult.Should().Be(PasswordVerificationResult.Success);

        // 2. Holds the 1 permit in a background task
        var backgroundHold = Task.Run(() => hasher.HashPassword(user, "slow-password"));

        // Wait a small moment to ensure the background task has acquired the permit
        await Task.Delay(20);

        try
        {
            // 3. Second concurrent call throws PasswordHashBulkheadRejectedException
            var act = () => hasher.HashPassword(user, "another-password");
            act.Should().Throw<PasswordHashBulkheadRejectedException>();

            ExamPrismDiagnostics.PasswordHashBulkheadRejectedCount.Should().Be(initialRejected + 1);
        }
        finally
        {
            // Unblock background task
            blocker.Set();
            await backgroundHold;
        }
    }

    [Fact]
    public async Task HybridCacheTokenRevocationService_And_JwtTenantValidator_RejectRevokedJti_AndRevokedUserTokens()
    {
        var initialJwtRejected = ExamPrismDiagnostics.JwtRevokedRejectedCount;

        // In-memory cache backing store for testing
        var cacheDict = new Dictionary<string, object>();
        var cacheService = Substitute.For<ICacheService>();

        cacheService.SetAsync(
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = callInfo.Arg<string>();
                var val = callInfo[1];
                cacheDict[key] = val;
                return ValueTask.CompletedTask;
            });

        cacheService.GetOrDefaultAsync<bool>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = callInfo.Arg<string>();
                return ValueTask.FromResult(cacheDict.TryGetValue(key, out var val) && val is bool b && b);
            });

        cacheService.GetOrDefaultAsync<DateTime?>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = callInfo.Arg<string>();
                if (cacheDict.TryGetValue(key, out var val) && val is DateTime dto)
                {
                    return ValueTask.FromResult<DateTime?>(dto);
                }
                return ValueTask.FromResult<DateTime?>(null);
            });

        var highLoadOptions = Options.Create(new IdentityHighLoadOptions
        {
            EnableTokenRevocationCheck = true
        });

        var revocationService = new HybridCacheTokenRevocationService(cacheService, highLoadOptions);

        // 1. RevokeTokenAsync("jti-123") -> IsTokenRevokedAsync("jti-123") is true while "jti-456" is false
        await revocationService.RevokeTokenAsync("jti-123");
        (await revocationService.IsTokenRevokedAsync("jti-123")).Should().BeTrue();
        (await revocationService.IsTokenRevokedAsync("jti-456")).Should().BeFalse();

        // 2. RevokeUserTokensAsync(userId, tenantId) at T1
        var userId = "user-abc-123";
        var tenantId = "tenant-xyz-789";
        var t1 = DateTime.UtcNow;

        await revocationService.RevokeUserTokensAsync(userId, tenantId);

        // Token with iat < T1 returns true, while iat > T1 returns false
        var tOld = t1.AddMinutes(-5);
        var tNew = t1.AddMinutes(5);

        (await revocationService.IsTokenRevokedAsync("jti-unrevoked-old", userId, tenantId, tOld)).Should().BeTrue();
        (await revocationService.IsTokenRevokedAsync("jti-unrevoked-new", userId, tenantId, tNew)).Should().BeFalse();

        // 3. Invokes JwtTenantValidator.ValidateAsync with principal carrying revoked jti-123
        var tenantGuid = Guid.NewGuid();
        var tenantInfo = new ExamPrismTenantInfo(tenantGuid.ToString("D"), "tenant-revocation")
        {
            TenantGuid = tenantGuid,
            Status = TenantStatus.Active
        };

        var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo));

        var validator = new JwtTenantValidator(accessor, revocationService, highLoadOptions);
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var jwtBearerOptions = new JwtBearerOptions();

        var httpContext = new DefaultHttpContext();
        var tokenContext = new TokenValidatedContext(httpContext, scheme, jwtBearerOptions)
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(CustomClaimTypes.TenantId, tenantInfo.Id),
                new Claim("jti", "jti-123"),
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            ], "Bearer"))
        };

        await validator.ValidateAsync(tokenContext);

        tokenContext.Result?.Failure.Should().NotBeNull();
        httpContext.Items[JwtTenantValidator.AuthFailureItemKey].Should().Be(JwtTenantValidator.TokenRevokedErrorCode);
        ExamPrismDiagnostics.JwtRevokedRejectedCount.Should().Be(initialJwtRejected + 1);
    }

    [Fact]
    public async Task CrossTenantTokenRevocation_And_JwtSecurityTokenValidFromFallback_WorkAsExpected()
    {
        var cacheDict = new Dictionary<string, object>();
        var cacheService = Substitute.For<ICacheService>();

        cacheService.SetAsync(
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<IEnumerable<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = callInfo.Arg<string>();
                var val = callInfo[1];
                cacheDict[key] = val;
                return ValueTask.CompletedTask;
            });

        cacheService.GetOrDefaultAsync<bool>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = callInfo.Arg<string>();
                return ValueTask.FromResult(cacheDict.TryGetValue(key, out var val) && val is bool b && b);
            });

        cacheService.GetOrDefaultAsync<DateTime?>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var key = callInfo.Arg<string>();
                if (cacheDict.TryGetValue(key, out var val) && val is DateTime dto)
                {
                    return ValueTask.FromResult<DateTime?>(dto);
                }
                return ValueTask.FromResult<DateTime?>(null);
            });

        var highLoadOptions = Options.Create(new IdentityHighLoadOptions
        {
            EnableTokenRevocationCheck = true
        });

        var revocationService = new HybridCacheTokenRevocationService(cacheService, highLoadOptions);

        var platformUserId = "user-global-admin";
        var tRevoke = DateTime.UtcNow;
        var tOld = tRevoke.AddMinutes(-5);
        var tNew = tRevoke.AddMinutes(5);

        // Revoke platform user globally (tenantId = null or "platform")
        await revocationService.RevokeUserTokensAsync(platformUserId, tenantId: null);

        // Cross-tenant check: token for tenant-123 issued before revocation must be revoked
        (await revocationService.IsTokenRevokedAsync("jti-tenant-123", platformUserId, "tenant-123", tOld)).Should().BeTrue();
        // Token issued after revocation is not revoked
        (await revocationService.IsTokenRevokedAsync("jti-tenant-123-new", platformUserId, "tenant-123", tNew)).Should().BeFalse();

        // Tenant-scoped revocation: revoking in tenant-a does NOT revoke access in tenant-b
        var tenantUserId = "user-tenant-scoped";
        await revocationService.RevokeUserTokensAsync(tenantUserId, tenantId: "tenant-a");
        (await revocationService.IsTokenRevokedAsync("jti-a", tenantUserId, "tenant-a", tOld)).Should().BeTrue();
        (await revocationService.IsTokenRevokedAsync("jti-b", tenantUserId, "tenant-b", tOld)).Should().BeFalse();

        // JwtTenantValidator fallback: missing iat claim falls back to SecurityToken.ValidFrom
        var tenantGuid = Guid.NewGuid();
        var tenantInfo = new ExamPrismTenantInfo(tenantGuid.ToString("D"), "tenant-fallback")
        {
            TenantGuid = tenantGuid,
            Status = TenantStatus.Active
        };

        var accessor = Substitute.For<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        accessor.MultiTenantContext.Returns(new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo));

        var validator = new JwtTenantValidator(accessor, revocationService, highLoadOptions);
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var jwtBearerOptions = new JwtBearerOptions();

        var httpContext = new DefaultHttpContext();
        var jwtSecurityToken = new JwtSecurityToken(
            issuer: "https://auth.examprism.com",
            audience: "examprism-api",
            claims:
            [
                new Claim(CustomClaimTypes.TenantId, tenantInfo.Id),
                new Claim(ClaimTypes.NameIdentifier, platformUserId)
            ],
            notBefore: tOld,
            expires: tRevoke.AddHours(1));

        var tokenContext = new TokenValidatedContext(httpContext, scheme, jwtBearerOptions)
        {
            Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(CustomClaimTypes.TenantId, tenantInfo.Id),
                new Claim(ClaimTypes.NameIdentifier, platformUserId)
                // Note: no "iat" claim here to force fallback to SecurityToken.ValidFrom
            ], "Bearer")),
            SecurityToken = jwtSecurityToken
        };

        await validator.ValidateAsync(tokenContext);

        tokenContext.Result?.Failure.Should().NotBeNull();
        httpContext.Items[JwtTenantValidator.AuthFailureItemKey].Should().Be(JwtTenantValidator.TokenRevokedErrorCode);
    }

    [Fact]
    public async Task AdaptiveTraceSampler_And_PrometheusMetricsEndpoint_SampleCorrectlyAndExposeAllV2Metrics()
    {
        // 1. Verifies AdaptiveTraceSampler with ratio 0.0 drops normal activities but samples recorded parent or critical/error
        var sampler = new AdaptiveTraceSampler(0.0);
        var traceId = ActivityTraceId.CreateRandom();

        // Normal activity -> Drop
        var normalParams = new SamplingParameters(
            default,
            traceId,
            "GET /api/v1/exams",
            ActivityKind.Server,
            null,
            null);
        sampler.ShouldSample(normalParams).Decision.Should().Be(SamplingDecision.Drop);

        // Recorded parent -> RecordAndSample
        var recordedParent = new ActivityContext(traceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var parentRecordedParams = new SamplingParameters(
            recordedParent,
            traceId,
            "GET /api/v1/exams",
            ActivityKind.Server,
            null,
            null);
        sampler.ShouldSample(parentRecordedParams).Decision.Should().Be(SamplingDecision.RecordAndSample);

        // Activity name contains "exam.critical" -> RecordAndSample
        var criticalNameParams = new SamplingParameters(
            default,
            traceId,
            "exam.critical.submission",
            ActivityKind.Server,
            null,
            null);
        sampler.ShouldSample(criticalNameParams).Decision.Should().Be(SamplingDecision.RecordAndSample);

        // Activity name contains "error" -> RecordAndSample
        var errorNameParams = new SamplingParameters(
            default,
            traceId,
            "payment.error.handler",
            ActivityKind.Server,
            null,
            null);
        sampler.ShouldSample(errorNameParams).Decision.Should().Be(SamplingDecision.RecordAndSample);

        // Tags contain "exam.critical" -> RecordAndSample
        var criticalTagParams = new SamplingParameters(
            default,
            traceId,
            "normal.operation",
            ActivityKind.Server,
            [new KeyValuePair<string, object?>("exam.critical", true)],
            null);
        sampler.ShouldSample(criticalTagParams).Decision.Should().Be(SamplingDecision.RecordAndSample);

        // Tags contain "error" -> RecordAndSample
        var errorTagParams = new SamplingParameters(
            default,
            traceId,
            "normal.operation",
            ActivityKind.Server,
            [new KeyValuePair<string, object?>("error.type", "timeout")],
            null);
        sampler.ShouldSample(errorTagParams).Decision.Should().Be(SamplingDecision.RecordAndSample);

        // 2. Calls GET /metrics via in-memory TestServer with UseAppTelemetry and asserts output contains all 6 v2 metrics
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddSingleton(Options.Create(new TelemetryOptions { EnablePrometheusEndpoint = true }));
        builder.WebHost.UseTestServer();

        var app = builder.Build();
        app.UseRouting();
        app.UseAppTelemetry();
        await app.StartAsync();

        try
        {
            var client = app.GetTestClient();
            var response = await client.GetAsync("/metrics");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var metricsContent = await response.Content.ReadAsStringAsync();
            metricsContent.Should().Contain("examprism_load_shedding_rejected_total");
            metricsContent.Should().Contain("examprism_auth_jwt_revoked_rejected_total");
            metricsContent.Should().Contain("examprism_auth_password_hash_bulkhead_rejected_total");
            metricsContent.Should().Contain("examprism_batcher_flushed_items_total");
            metricsContent.Should().Contain("examprism_db_tenant_datasources_active");
            metricsContent.Should().Contain("examprism_http_in_flight_requests");
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
