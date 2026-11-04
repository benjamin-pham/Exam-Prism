namespace ExamPrism.Tests.Infrastructure;

using System.Net;
using Common.Telemetry;
using ExamPrism.Infrastructure.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
}

