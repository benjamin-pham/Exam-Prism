namespace ExamPrism.Features.Platform.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Domain.Tenants;
using ExamPrism.Domain.Tenants.Specifications;
using ExamPrism.Infrastructure.Persistence;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentValidation;

public record UpdateTenantCommand(Guid Id, string Name);

public sealed class UpdateTenantCommandValidator : AbstractValidator<UpdateTenantCommand>
{
    public UpdateTenantCommandValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty().WithErrorCode("REQUIRED");

        RuleFor(x => x.Name)
            .NotEmpty().WithErrorCode("REQUIRED")
            .MaximumLength(200).WithErrorCode("MAX_LENGTH");
    }
}

public sealed class UpdateTenantHandler(
    CatalogDbContext db,
    ITenantCacheInvalidator cacheInvalidator,
    TimeProvider timeProvider) : IHandler<UpdateTenantCommand, TenantDetailResult>
{
    public async Task<Result<TenantDetailResult>> Handle(UpdateTenantCommand command, CancellationToken ct = default)
    {
        var tenant = await db.QueryFirstOrDefaultAsync(new TenantByIdSpec(command.Id, track: true), ct);
        if (tenant is null)
        {
            return TenantErrors.TenantNotFound();
        }

        var renameResult = tenant.Rename(command.Name, timeProvider.GetUtcNow());
        if (renameResult.IsFailure)
        {
            return renameResult.Error;
        }

        await db.SaveChangesAsync(ct);
        await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct);

        return new TenantDetailResult(
            tenant.Id,
            tenant.Slug,
            tenant.Name,
            tenant.Status,
            tenant.FailureReason,
            tenant.CreatedAt,
            tenant.ProvisionedAt,
            tenant.UpdatedAt);
    }
}
