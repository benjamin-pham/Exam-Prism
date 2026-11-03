namespace ExamPrism.Endpoints.Platform.Tenants;

using Common.Endpoints;
using Common.Results;
using Common.Security;
using ExamPrism.Features.Platform.Tenants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class UpdateTenantEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPatch("/api/platform/tenants/{id:guid}", async (
            Guid id,
            UpdateTenantRequest request,
            IMessageBus bus,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            var command = new UpdateTenantCommand(id, request.Name);
            var result = await bus.InvokeAsync<Result<TenantDetailResult>>(command, ct);
            if (result.IsFailure)
            {
                return result.ToProblemResult(httpContext);
            }

            return Results.Ok(result.Value);
        })
        .RequireAuthorization(PlatformRoles.PolicyName)
        .RequirePlatform()
        .WithTags("Platform Tenants")
        .WithSummary("Updates organization details");
    }
}
