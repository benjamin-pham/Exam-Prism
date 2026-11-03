namespace ExamPrism.Endpoints.Tenants;

using Common.Endpoints;
using Common.RateLimiting;
using Common.Results;
using ExamPrism.Features.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class ProvisionTenantPublicEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tenants", async (
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
        .WithTags("Tenant Onboarding")
        .WithSummary("Registers a new organization workspace and initiates background provisioning")
        .RequirePlatform()
        .RequireRateLimiting(RateLimitPolicies.Auth);
    }
}
