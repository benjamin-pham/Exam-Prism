namespace ExamPrism.Endpoints.Auth;

using Common.Endpoints;
using ExamPrism.Features.Auth.Login;
using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Wolverine;

public sealed class LoginEndpoint : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/login", Handle)
            .WithTags("Authentication")
            .WithSummary("Authenticates user credentials and sets auth cookies");
    }

    private static async Task<IResult> Handle(
        LoginCommand command,
        IMessageBus bus,
        HttpResponse response,
        IOptions<JwtOptions> jwtOptions,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var result = await bus.InvokeAsync<LoginResult>(command, ct);
        AuthCookieHelper.SetAuthCookies(response, result.AccessToken, result.RefreshToken, jwtOptions.Value, timeProvider);
        return Results.Ok(result);
    }
}
