namespace ExamPrism.Endpoints.Platform.Tenants;

using Common.Endpoints;
using Common.Results;
using Common.Security;
using ExamPrism.Features.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class CreatePlatformTenantEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/platform/tenants", async (
            CreateTenantCommand command,
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<TenantProvisioningAcceptedResult>>(command, ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.Accepted($"/api/tenants/{result.Value.Slug}/status", result.Value);
        })
        .RequireAuthorization(PlatformRoles.PolicyName)
        .RequirePlatform()
        .WithTags("Platform Tenants")
        .WithSummary("Creates an organization workspace as a platform admin and initiates provisioning");
    }
}
