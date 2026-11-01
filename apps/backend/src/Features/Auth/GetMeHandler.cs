namespace ExamPrism.Features.Auth;

using Common.Security;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Wolverine.Http;

public record GetMeResponse(Guid Id, string Email);

public static class GetMeHandler
{
    [WolverineGet("/api/auth/me")]
    [Authorize]
    public static async Task<IResult> Handle(
        IUserContext userContext,
        UserManager<ApplicationUser> userManager)
    {
        if (userContext.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new GetMeResponse(user.Id, user.Email!));
    }
}
