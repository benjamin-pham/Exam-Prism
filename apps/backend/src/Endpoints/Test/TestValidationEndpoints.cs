namespace ExamPrism.Endpoints.Test;

using Common.Endpoints;
using Common.Localization;
using Common.Validation;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
        var group = app.MapGroup("/api/test/validation");

        group.MapPost("", (TestValidationRequest request) =>
            Results.Ok(new { message = "Valid", name = request.Name }));

        group.MapPost("/skip", [SkipValidation] (TestValidationRequest request) =>
            Results.Ok(new { message = "Skipped", name = request.Name }));

        group.MapPost("/unvalidated", (UnvalidatedRequest request) =>
            Results.Ok(new { message = "Unvalidated", info = request.Info }));

        group.MapPost("/multi-param", (TestValidationRequest request, [Microsoft.AspNetCore.Mvc.FromServices] Common.Security.IUserContext? userContext) =>
            Results.Ok(new { message = "MultiParam", name = request.Name }));
    }
}

