namespace ExamPrism.Endpoints.Auth;

using Common.Endpoints;
using ExamPrism.Features.Auth.Register;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Wolverine;

public sealed class RegisterEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/register", Handle)
            .WithTags("Authentication")
            .WithSummary("Registers a new user and sets auth cookies");
    }

    private static async Task<IResult> Handle(
        RegisterCommand command,
        IMessageBus bus,
        HttpResponse response,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await bus.InvokeAsync<RegisterResult>(command, ct);
        AuthCookieHelper.SetAuthCookies(response, result.AccessToken, result.RefreshToken, jwtOptions.Value, timeProvider);
        return Results.Created("/api/auth/me", result);
    }
}

