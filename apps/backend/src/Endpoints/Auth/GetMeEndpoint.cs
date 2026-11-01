namespace ExamPrism.Endpoints.Auth;

using System.Security.Claims;
using Common.Endpoints;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;

public record GetMeResponse(Guid Id, string Email);

public sealed class GetMeEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/me", HandleAsync)
            .RequireAuthorization()
            .WithTags("Authentication")
            .WithName("GetMe")
            .WithDescription("Gets currently authenticated user details");
    }

    private static async Task<IResult> HandleAsync(
        ClaimsPrincipal principal,
        UserManager<ApplicationUser> userManager)
    {
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId) || !Guid.TryParse(userId, out var guid))
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(guid.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new GetMeResponse(user.Id, user.Email!));
    }
}
