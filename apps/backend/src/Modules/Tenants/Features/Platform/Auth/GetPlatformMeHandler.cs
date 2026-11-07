namespace ExamPrism.Features.Platform.Auth;

using Common.Handlers;
using Common.Results;
using Common.Security;
using ExamPrism.Domain.Identity;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

public record GetPlatformMeQuery;

public record GetPlatformMeResult(Guid Id, string Email, IReadOnlyList<string> Roles);

public sealed class GetPlatformMeHandler(
    IUserContext userContext,
    UserManager<PlatformUser> userManager) : IHandler<GetPlatformMeQuery, GetPlatformMeResult>
{
    public async Task<Result<GetPlatformMeResult>> Handle(GetPlatformMeQuery query, CancellationToken ct = default)
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

        var roles = await userManager.GetRolesAsync(user);
        return new GetPlatformMeResult(user.Id, user.Email!, roles.ToList().AsReadOnly());
    }
}
