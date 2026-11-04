namespace ExamPrism.Tests.Infrastructure;

using System.Net;
using System.Net.Http.Json;
using ExamPrism.Features.Health;
using ExamPrism.Infrastructure.Health;
using ExamPrism.Infrastructure.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using global::Tests.Infrastructure;
using Xunit;

public class EdgeAndHealthTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public EdgeAndHealthTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public void ReverseProxySetup_ResolvesClientIpFromCfConnectingIp_XRealIp_AndForwardedFor()
    {
        // 1. CF-Connecting-IP takes highest precedence
        var ctx1 = new DefaultHttpContext();
        ctx1.Request.Headers["CF-Connecting-IP"] = "203.0.113.195";
        ctx1.Request.Headers["X-Real-IP"] = "198.51.100.1";
        ctx1.Request.Headers["X-Forwarded-For"] = "192.0.2.1, 10.0.0.2";
        ctx1.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");

        ReverseProxySetup.ResolveClientIp(ctx1).Should().Be("203.0.113.195");

        // 2. X-Real-IP takes precedence over X-Forwarded-For and RemoteIpAddress
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers["X-Real-IP"] = "198.51.100.1";
        ctx2.Request.Headers["X-Forwarded-For"] = "192.0.2.1, 10.0.0.2";
        ctx2.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");

        ReverseProxySetup.ResolveClientIp(ctx2).Should().Be("198.51.100.1");

        // 3. X-Forwarded-For first entry takes precedence over RemoteIpAddress
        var ctx3 = new DefaultHttpContext();
        ctx3.Request.Headers["X-Forwarded-For"] = "192.0.2.1, 10.0.0.2";
        ctx3.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");

        ReverseProxySetup.ResolveClientIp(ctx3).Should().Be("192.0.2.1");

        // 4. RemoteIpAddress fallback when no proxy headers present
        var ctx4 = new DefaultHttpContext();
        ctx4.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.50");

        ReverseProxySetup.ResolveClientIp(ctx4).Should().Be("192.168.1.50");

        // 5. Unknown fallback when no IP information is available
        var ctx5 = new DefaultHttpContext();

        ReverseProxySetup.ResolveClientIp(ctx5).Should().Be("unknown");

        // 6. Verify ForwardedHeadersOptions configuration with private CIDR networks
        var services = new ServiceCollection();
        services.AddAppReverseProxy();
        using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        options.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedFor);
        options.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedProto);
        options.ForwardedHeaders.Should().HaveFlag(ForwardedHeaders.XForwardedHost);

#pragma warning disable ASPDEPR005, CS0618
        options.KnownNetworks.Should().Contain(n => n.Prefix.ToString() == "10.0.0.0" && n.PrefixLength == 8);
        options.KnownNetworks.Should().Contain(n => n.Prefix.ToString() == "172.16.0.0" && n.PrefixLength == 12);
        options.KnownNetworks.Should().Contain(n => n.Prefix.ToString() == "192.168.0.0" && n.PrefixLength == 16);
#pragma warning restore ASPDEPR005, CS0618
    }

    [Fact]
    public async Task HealthEndpoints_LiveAlwaysReturns200_WhileReadyAndHealthReturn503WhenHostIsDraining()
    {
        var drainState = _factory.Services.GetRequiredService<HostDrainState>();

        try
        {
            // Initial state: not draining
            drainState.IsDraining.Should().BeFalse();

            var liveResponseBefore = await _client.GetAsync("/health/live");
            liveResponseBefore.StatusCode.Should().Be(HttpStatusCode.OK);
            var liveBodyBefore = await liveResponseBefore.Content.ReadFromJsonAsync<GetHealthResult>();
            liveBodyBefore.Should().NotBeNull();
            liveBodyBefore!.Status.Should().Be("Healthy");

            var readyResponseBefore = await _client.GetAsync("/health/ready");
            readyResponseBefore.StatusCode.Should().Be(HttpStatusCode.OK);
            var readyBodyBefore = await readyResponseBefore.Content.ReadFromJsonAsync<GetHealthResult>();
            readyBodyBefore.Should().NotBeNull();
            readyBodyBefore!.Status.Should().Be("Healthy");

            var healthResponseBefore = await _client.GetAsync("/health");
            healthResponseBefore.StatusCode.Should().Be(HttpStatusCode.OK);
            var healthBodyBefore = await healthResponseBefore.Content.ReadFromJsonAsync<GetHealthResult>();
            healthBodyBefore.Should().NotBeNull();
            healthBodyBefore!.Status.Should().Be("Healthy");

            // Mark host as draining
            drainState.MarkDraining();
            drainState.IsDraining.Should().BeTrue();

            // /health/live must STILL return 200 OK
            var liveResponseDuring = await _client.GetAsync("/health/live");
            liveResponseDuring.StatusCode.Should().Be(HttpStatusCode.OK);
            var liveBodyDuring = await liveResponseDuring.Content.ReadFromJsonAsync<GetHealthResult>();
            liveBodyDuring.Should().NotBeNull();
            liveBodyDuring!.Status.Should().Be("Healthy");

            // /health/ready must return 503 immediately
            var readyResponseDuring = await _client.GetAsync("/health/ready");
            readyResponseDuring.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            // /health must return 503 immediately
            var healthResponseDuring = await _client.GetAsync("/health");
            healthResponseDuring.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            drainState.Reset();
        }

        // Verify restoration after reset
        var healthResponseAfter = await _client.GetAsync("/health");
        healthResponseAfter.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
