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

public sealed class MigrateSingleTenantEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/platform/tenants/{id:guid}/migrate", async (
            Guid id,
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var result = await bus.InvokeAsync<Result<TenantMigrationItemResult>>(new MigrateSingleTenantCommand(id), ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.Ok(result.Value);
        })
        .RequireAuthorization(PlatformRoles.PolicyName)
        .RequirePlatform()
        .WithTags("Platform Tenants")
        .WithSummary("Migrates database schema for a single active organization workspace");
    }
}
