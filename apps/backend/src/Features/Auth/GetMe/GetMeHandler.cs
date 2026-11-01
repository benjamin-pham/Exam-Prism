namespace ExamPrism.Features.Auth.GetMe;

using Common.Exceptions;
using Common.Handlers;
using Common.Security;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

public sealed class GetMeHandler : IHandler
{
    public static async Task<GetMeResult> Handle(
        GetMeQuery query,
        IUserContext userContext,
        UserManager<ApplicationUser> userManager,
        CancellationToken ct = default)
    {
        if (userContext.UserId is not { } userId)
        {
            throw new UnauthorizedAccessException("User is not authenticated.");
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            throw new NotFoundException($"User '{userId}' was not found.");
        }

        return new GetMeResult(user.Id, user.Email!);
    }
}
