namespace ExamPrism.Features.Auth;

using System.ComponentModel.DataAnnotations;
using Common.Endpoints;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

public record LoginRequest(
    [Required, EmailAddress] string Email,
    [Required] string Password);

public record LoginResponse(Guid Id, string Email);

public sealed class LoginEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", HandleAsync)
            .WithTags("Authentication")
            .WithName("Login")
            .WithDescription("Authenticates user credentials and sets auth cookies");
    }

    private static async Task<IResult> HandleAsync(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        ITokenService tokenService,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider,
        HttpResponse response)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null || !await userManager.CheckPasswordAsync(user, request.Password))
        {
            return Results.Unauthorized();
        }

        var accessToken = tokenService.GenerateAccessToken(user);
        var (rawRefresh, refreshHash) = tokenService.GenerateRefreshToken();

        var now = timeProvider.GetUtcNow();
        var refreshToken = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = refreshHash,
            CreatedAt = now,
            ExpiresAt = now.AddDays(jwtOptions.Value.RefreshTokenExpirationDays)
        };

        db.RefreshTokens.Add(refreshToken);
        await db.SaveChangesAsync();

        AuthCookieHelper.SetAuthCookies(response, accessToken, rawRefresh, jwtOptions.Value, timeProvider);

        return Results.Ok(new LoginResponse(user.Id, user.Email!));
    }
}

