namespace ExamPrism.Endpoints.Platform.Tenants;

using Common.Endpoints;
using Common.MultiTenancy;
using Common.Results;
using Common.Security;
using ExamPrism.Features.Platform.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class MigrateAllTenantsEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/platform/tenants/migrate", async (
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<FleetMigrationSummaryResult>>(new MigrateAllTenantsCommand(), ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.Ok(result.Value);
        })
        .RequireAuthorization(PlatformRoles.PolicyName)
        .RequirePlatform()
        .WithTags("Platform Tenants")
        .WithSummary("Migrates database schema across all active organization workspaces");
    }
}
