namespace ExamPrism.Endpoints.Tenants;

using Common.Endpoints;
using Common.RateLimiting;
using Common.Results;
using ExamPrism.Features.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class CheckTenantSlugEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/tenants/check-slug/{slug}", async (
            string slug,
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<CheckTenantSlugResult>>(new CheckTenantSlugQuery(slug), ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.Ok(result.Value);
        })
        .WithTags("Tenant Onboarding")
        .WithSummary("Checks availability of an organization subdomain slug")
        .RequirePlatform()
        .RequireRateLimiting(RateLimitPolicies.Auth);
    }
}
