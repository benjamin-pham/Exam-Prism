namespace ExamPrism.Features.Test;

using Common.Localization;
using Common.Security;
using Common.Validation;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using Wolverine.Http;

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

public static class TestValidationHandlers
{
    [WolverinePost("/api/test/validation")]
    public static IResult PostValid(TestValidationRequest request) =>
        Results.Ok(new { message = "Valid", name = request.Name });

    [WolverinePost("/api/test/validation/skip")]
    [SkipValidation]
    public static IResult PostSkip(TestValidationRequest request) =>
        Results.Ok(new { message = "Skipped", name = request.Name });

    [WolverinePost("/api/test/validation/unvalidated")]
    public static IResult PostUnvalidated(UnvalidatedRequest request) =>
        Results.Ok(new { message = "Unvalidated", info = request.Info });
}
