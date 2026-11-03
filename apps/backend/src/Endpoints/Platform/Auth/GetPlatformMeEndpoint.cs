namespace ExamPrism.Endpoints.Platform.Auth;

using Common.Endpoints;
using Common.Results;
using Common.Security;
using ExamPrism.Features.Platform.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class GetPlatformMeEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/platform/auth/me", async (
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<GetPlatformMeResult>>(new GetPlatformMeQuery(), ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.Ok(result.Value);
        })
        .RequireAuthorization(PlatformRoles.PolicyName)
        .RequirePlatform()
        .WithTags("Platform Authentication")
        .WithSummary("Gets currently authenticated platform administrator details");
    }
}
