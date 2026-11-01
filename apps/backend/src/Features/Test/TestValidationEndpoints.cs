namespace ExamPrism.Features.Test;

using Common.Endpoints;
using Common.Localization;
using Common.Security;
using Common.Validation;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;

public record TestValidationRequest(string Name);

public class TestValidationRequestValidator : AbstractValidator<TestValidationRequest>
{
    public TestValidationRequestValidator(IStringLocalizer<SharedResources> localizer)
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .WithErrorCode("NAME_REQUIRED")
            .WithMessage(_ => localizer["NameRequired"]);
    }
}

public record UnvalidatedRequest(string? Info);

public sealed class TestValidationEndpoints : IEndpoint
{
    public static void MapEndpoint(IEndpointRouteBuilder app)
    {
        var env = (app as WebApplication)?.Environment
            ?? app.ServiceProvider?.GetService<IHostEnvironment>();

        if (env is null || !env.IsDevelopment())
        {
            return;
        }

        app.MapPost("/api/test/validation", (TestValidationRequest request) =>
            Results.Ok(new { message = "Valid", name = request.Name }));

        app.MapPost("/api/test/validation/skip", [SkipValidation] (TestValidationRequest request) =>
            Results.Ok(new { message = "Skipped", name = request.Name }));

        app.MapPost("/api/test/validation/unvalidated", (UnvalidatedRequest request) =>
            Results.Ok(new { message = "Unvalidated", info = request.Info }));

        app.MapPost("/api/test/validation/multi-param", (TestValidationRequest request, [FromServices] IUserContext? userContext) =>
            Results.Ok(new { message = "MultiParam", name = request.Name }));
    }
}
