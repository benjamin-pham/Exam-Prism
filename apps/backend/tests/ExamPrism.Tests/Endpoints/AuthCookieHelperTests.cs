namespace ExamPrism.Tests.Endpoints;

using ExamPrism.Endpoints.Auth;
using ExamPrism.Infrastructure.Identity;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class AuthCookieHelperTests
{
    [Fact]
    public void SetAuthCookies_SetsBothCookiesWithHttpOnlyAndPath()
    {
        var context = new DefaultHttpContext();
        var options = new JwtOptions { AccessTokenExpirationMinutes = 15, RefreshTokenExpirationDays = 7 };
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

        AuthCookieHelper.SetAuthCookies(context.Response, "access-token-val", "refresh-token-val", options, timeProvider);

        var setCookieHeaders = context.Response.Headers.SetCookie.ToArray();
        setCookieHeaders.Should().HaveCount(2);

        var accessCookie = setCookieHeaders.First(h => h != null && h.StartsWith("access_token="));
        accessCookie.Should().Contain("httponly");
        accessCookie.Should().Contain("path=/");
        accessCookie.Should().Contain("samesite=lax");

        var refreshCookie = setCookieHeaders.First(h => h != null && h.StartsWith("refresh_token="));
        refreshCookie.Should().Contain("httponly");
        refreshCookie.Should().Contain("path=/");
        refreshCookie.Should().Contain("samesite=lax");
    }

    [Fact]
    public void ClearAuthCookies_DeletesBothCookies()
    {
        var context = new DefaultHttpContext();

        AuthCookieHelper.ClearAuthCookies(context.Response);

        var setCookieHeaders = context.Response.Headers.SetCookie.ToArray();
        setCookieHeaders.Should().HaveCount(2);
        setCookieHeaders.Should().Contain(h => h.StartsWith("access_token=") && (h.Contains("expires=") || h.Contains("max-age=0")));
        setCookieHeaders.Should().Contain(h => h.StartsWith("refresh_token=") && (h.Contains("expires=") || h.Contains("max-age=0")));
    }
}
