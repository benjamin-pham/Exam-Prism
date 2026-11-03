namespace ExamPrism.Features.Platform.Tenants;

using Common.MultiTenancy;
using FluentValidation;

public record TenantSummaryItem(
    Guid Id,
    string Slug,
    string Name,
    TenantStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProvisionedAt,
    DateTimeOffset UpdatedAt);

public record TenantDetailResult(
    Guid Id,
    string Slug,
    string Name,
    TenantStatus Status,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProvisionedAt,
    DateTimeOffset UpdatedAt);

public record UpdateTenantRequest(string Name);

public sealed class UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>
{
    public UpdateTenantRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithErrorCode("REQUIRED")
            .MaximumLength(200).WithErrorCode("MAX_LENGTH");
    }
}
