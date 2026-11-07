namespace ExamPrism.Endpoints.Tenants;

using Common.Endpoints;
using Common.Results;
using ExamPrism.Features.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class GetTenantProvisioningStatusEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tenants/{slug}/status", async (
            string slug,
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<GetTenantProvisioningStatusResult>>(new GetTenantProvisioningStatusQuery(slug), ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.Ok(result.Value);
        })
        .WithTags("Tenant Onboarding")
        .WithSummary("Polls provisioning status of an organization workspace")
        .RequirePlatform();
    }
}
