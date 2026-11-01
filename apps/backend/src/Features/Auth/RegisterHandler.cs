namespace ExamPrism.Features.Auth;

using System.ComponentModel.DataAnnotations;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Wolverine.Http;

public record RegisterRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(6)] string Password);

public record RegisterResponse(Guid Id, string Email);

public static class RegisterHandler
{
    [WolverinePost("/api/auth/register")]
    [Tags("Authentication")]
    [EndpointSummary("Registers a new user and sets auth cookies")]
    public static async Task<IResult> Handle(
        RegisterRequest request,
        UserManager<ApplicationUser> userManager,
        AppDbContext db,
        ITokenService tokenService,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider,
        HttpResponse response)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                { "Request", ["Email and password are required."] }
            });
        }

        var existing = await userManager.FindByEmailAsync(request.Email);
        if (existing is not null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                { nameof(request.Email), ["Email is already registered."] }
            });
        }

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            Email = request.Email,
            UserName = request.Email
        };

        var result = await userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            var errors = result.Errors
                .GroupBy(e => e.Code)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());
            return Results.ValidationProblem(errors);
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

        return Results.Created("/api/auth/me", new RegisterResponse(user.Id, user.Email!));
    }
}
