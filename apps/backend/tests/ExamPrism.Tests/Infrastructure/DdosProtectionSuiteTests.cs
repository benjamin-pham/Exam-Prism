namespace ExamPrism.Tests.Infrastructure;

using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Net;
using System.Security.Claims;
using System.Text;
using Common.Security;
using Common.Telemetry;
using ExamPrism.Infrastructure.Http;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
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
}

