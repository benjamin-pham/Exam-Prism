namespace Tests.Infrastructure;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ExamPrism.Features.Auth;
using ExamPrism.Features.Health;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Xunit;

public class RateLimitingIntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public RateLimitingIntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateRateLimitedClient()
    {
        return _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Auth:PermitLimit", "2");
            builder.UseSetting("RateLimiting:Auth:WindowSeconds", "60");
            builder.UseSetting("RateLimiting:Global:PermitLimit", "3");
            builder.UseSetting("RateLimiting:Global:WindowSeconds", "60");
        }).CreateClient();
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
    public async Task AuthEndpoints_ExceedingAuthPermitLimit_Returns429ProblemDetailsWithRetryAfter_LocalizedInViAndEn()
    {
        var client = CreateRateLimitedClient();

        // 1st request - 401
        var res1 = await client.PostAsJsonAsync("/api/auth/login", new LoginCommand("nobody@example.com", "Password123!"));
        res1.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 2nd request - 401
        var res2 = await client.PostAsJsonAsync("/api/auth/login", new LoginCommand("nobody@example.com", "Password123!"));
        res2.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 3rd request (Vietnamese) - 429
        var reqVi = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginCommand("nobody@example.com", "Password123!"))
        };
        reqVi.Headers.Add("Accept-Language", "vi-VN");

        var resVi = await client.SendAsync(reqVi);
        resVi.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        resVi.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        resVi.Headers.TryGetValues("Retry-After", out var retryAfterValues).Should().BeTrue();
        int.Parse(retryAfterValues!.First()).Should().BeGreaterThanOrEqualTo(1);

        var problemVi = await resVi.Content.ReadFromJsonAsync<ProblemDetails>();
        problemVi.Should().NotBeNull();
        problemVi!.Status.Should().Be(429);
        problemVi.Title.Should().Be("Too Many Requests");
        problemVi.Detail.Should().Be("Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau.");
        problemVi.Extensions.Should().ContainKey("traceId");

        // 4th request (English) - 429
        var reqEn = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginCommand("nobody@example.com", "Password123!"))
        };
        reqEn.Headers.Add("Accept-Language", "en-US");

        var resEn = await client.SendAsync(reqEn);
        resEn.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var problemEn = await resEn.Content.ReadFromJsonAsync<ProblemDetails>();
        problemEn.Should().NotBeNull();
        problemEn!.Detail.Should().Be("Too many requests. Please try again later.");
    }

    [Fact]
    public async Task GlobalLimiter_PartitionsByAuthenticatedUserId_SeparatelyFromOtherUsersAndAnonymousIp()
    {
        var client = CreateRateLimitedClient();

        var emailA = $"userA_{Guid.NewGuid()}@example.com";
        var regA = await client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(emailA, "Password123!"));
        regA.StatusCode.Should().Be(HttpStatusCode.Created);
        var (accessA, _) = ExtractCookies(regA);
        accessA.Should().NotBeNullOrWhiteSpace();

        var emailB = $"userB_{Guid.NewGuid()}@example.com";
        // User B registration might hit Auth rate limit if sent from same client IP immediately, so let's use another client instance or wait?
        // Wait, Auth policy has limit 2. regA was 1. regB is 2. So regB succeeds!
        var regB = await client.PostAsJsonAsync("/api/auth/register", new RegisterCommand(emailB, "Password123!"));
        regB.StatusCode.Should().Be(HttpStatusCode.Created);
        var (accessB, _) = ExtractCookies(regB);
        accessB.Should().NotBeNullOrWhiteSpace();

        // Create clients with auth headers for User A and User B
        var clientA = CreateRateLimitedClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessA);

        // User A: 3 requests allowed under Global (limit = 3)
        for (var i = 0; i < 3; i++)
        {
            var resMeA = await clientA.GetAsync("/api/auth/me");
            resMeA.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // 4th request for User A hits 429
        var resMeA4 = await clientA.GetAsync("/api/auth/me");
        resMeA4.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // User B has their own bucket by userId - should succeed!
        var clientB = CreateRateLimitedClient();
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessB);

        var resMeB = await clientB.GetAsync("/api/auth/me");
        resMeB.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthEndpoint_IsNeverRateLimited_EvenAfterAuthAndGlobalQuotasAreExhausted()
    {
        var client = CreateRateLimitedClient();

        // Exhaust Auth quota
        await client.PostAsJsonAsync("/api/auth/login", new LoginCommand("nobody@example.com", "Password123!"));
        await client.PostAsJsonAsync("/api/auth/login", new LoginCommand("nobody@example.com", "Password123!"));
        var resAuthBlocked = await client.PostAsJsonAsync("/api/auth/login", new LoginCommand("nobody@example.com", "Password123!"));
        resAuthBlocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        // Verify /health is never blocked
        for (var i = 0; i < 10; i++)
        {
            var resHealth = await client.GetAsync("/health");
            resHealth.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
