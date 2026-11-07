namespace ExamPrism.Endpoints.Platform.Tenants;

using Common.Endpoints;
using Common.Results;
using Common.Security;
using ExamPrism.Features.Platform.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class RetryTenantProvisioningEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/platform/tenants/{id:guid}/retry-provisioning", async (
            Guid id,
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<TenantDetailResult>>(new RetryTenantProvisioningCommand(id), ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.Accepted($"/api/platform/tenants/{id}", result.Value);
        })
        .RequireAuthorization(PlatformRoles.PolicyName)
        .RequirePlatform()
        .WithTags("Platform Tenants")
        .WithSummary("Retries provisioning for a failed or in-progress organization workspace");
    }
}
