namespace ExamPrism.Features.Auth;

using Common.Handlers;
using Common.Results;
using Common.Security;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

public record GetMeQuery;

public record GetMeResult(Guid Id, string Email);

public sealed class GetMeHandler(
    IUserContext userContext,
    UserManager<ApplicationUser> userManager) : IHandler<GetMeQuery, GetMeResult>
{
    public async Task<Result<GetMeResult>> Handle(GetMeQuery query, CancellationToken ct = default)
    {
        if (userContext.UserId is not { } userId)
        {
            return Error.Unauthorized("UNAUTHORIZED", "User is not authenticated.");
        }

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Error.NotFound("USER_NOT_FOUND", $"User '{userId}' was not found.");
        }

        return new GetMeResult(user.Id, user.Email!);
    }
}
