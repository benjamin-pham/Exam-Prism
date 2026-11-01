namespace ExamPrism.Endpoints.Auth;

using Common.Endpoints;
using ExamPrism.Features.Auth.GetMe;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine;

public sealed class GetMeEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/me", Handle)
            .RequireAuthorization()
            .WithTags("Authentication")
            .WithSummary("Gets currently authenticated user details");
    }

    private static async Task<IResult> Handle(
        IMessageBus bus,
        CancellationToken ct)
    {
        var result = await bus.InvokeAsync<GetMeResult>(new GetMeQuery(), ct);
        return Results.Ok(result);
    }
}

