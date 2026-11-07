namespace ExamPrism.Endpoints.Platform.Tenants;

using Common.Endpoints;
using Common.Results;
using Common.Security;
using ExamPrism.Features.Platform.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class ListTenantsEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/platform/tenants", async (
            [AsParameters] ListTenantsQuery query,
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<ListTenantsResult>>(query, ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.Ok(result.Value);
        })
        .RequireAuthorization(PlatformRoles.PolicyName)
        .RequirePlatform()
        .WithTags("Platform Tenants")
        .WithSummary("Lists organizations with pagination and filtering");
    }
}
