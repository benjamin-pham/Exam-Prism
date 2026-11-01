namespace ExamPrism.Features.Auth;

using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;

public static class AuthCookieHelper
{
    public const string AccessTokenCookieName = "access_token";
    public const string RefreshTokenCookieName = "refresh_token";

    public static void SetAuthCookies(
        HttpResponse response,
        string accessToken,
        string rawRefreshToken,
        JwtOptions options,
        TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();

        response.Cookies.Append(AccessTokenCookieName, accessToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = now.AddMinutes(options.AccessTokenExpirationMinutes)
        });

        response.Cookies.Append(RefreshTokenCookieName, rawRefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = now.AddDays(options.RefreshTokenExpirationDays)
        });
    }

    public static void ClearAuthCookies(HttpResponse response)
    {
        response.Cookies.Delete(AccessTokenCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });

        response.Cookies.Delete(RefreshTokenCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
    }
}
