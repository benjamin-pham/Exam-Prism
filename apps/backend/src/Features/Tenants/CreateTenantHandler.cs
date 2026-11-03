namespace ExamPrism.Features.Tenants;

using System.ComponentModel.DataAnnotations;
using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Infrastructure.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wolverine;

public record CreateTenantCommand(
    [Required] string Slug,
    [Required] string Name,
    [Required, EmailAddress] string OwnerEmail,
    [Required, MinLength(6)] string OwnerPassword);

public sealed class CreateTenantCommandValidator : AbstractValidator<CreateTenantCommand>
{
    public CreateTenantCommandValidator()
    {
        RuleFor(x => x.Slug)
            .NotEmpty().WithErrorCode("REQUIRED")
            .MaximumLength(63).WithErrorCode("MAX_LENGTH")
            .Must(s => TenantSlugValidator.IsValid(s?.Trim().ToLowerInvariant())).WithErrorCode("INVALID_SLUG");

        RuleFor(x => x.Name)
            .NotEmpty().WithErrorCode("REQUIRED")
            .MaximumLength(200).WithErrorCode("MAX_LENGTH");

        RuleFor(x => x.OwnerEmail)
            .NotEmpty().WithErrorCode("REQUIRED")
            .EmailAddress().WithErrorCode("INVALID_EMAIL")
            .MaximumLength(256).WithErrorCode("MAX_LENGTH");

        RuleFor(x => x.OwnerPassword)
            .NotEmpty().WithErrorCode("REQUIRED")
            .MinimumLength(6).WithErrorCode("PASSWORD_TOO_SHORT")
            .Must(p => p != null && p.Any(char.IsDigit)).WithErrorCode("PASSWORD_REQUIRES_DIGIT")
            .Must(p => p != null && p.Any(char.IsLower)).WithErrorCode("PASSWORD_REQUIRES_LOWER");
    }
}

public record TenantProvisioningAcceptedResult(
    Guid Id,
    string Slug,
    string Name,
    TenantStatus Status,
    DateTimeOffset CreatedAt);

public sealed class CreateTenantHandler(
    CatalogDbContext db,
    IConnectionStringProtector protector,
    ITenantCacheInvalidator cacheInvalidator,
    IMessageBus bus,
    IOptions<MultiTenancyOptions> options,
    TimeProvider timeProvider) : IHandler<CreateTenantCommand, TenantProvisioningAcceptedResult>
{
    public async Task<Result<TenantProvisioningAcceptedResult>> Handle(CreateTenantCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.OwnerPassword))
        {
            return Error.Validation(nameof(command.OwnerPassword), "REQUIRED", "Owner password is required.");
        }

        if (command.OwnerPassword.Length < 6)
        {
            return Error.Validation(nameof(command.OwnerPassword), "PASSWORD_TOO_SHORT", "Owner password must be at least 6 characters long.");
        }

        if (!command.OwnerPassword.Any(char.IsDigit))
        {
            return Error.Validation(nameof(command.OwnerPassword), "PASSWORD_REQUIRES_DIGIT", "Owner password must contain at least one digit.");
        }

        if (!command.OwnerPassword.Any(char.IsLower))
        {
            return Error.Validation(nameof(command.OwnerPassword), "PASSWORD_REQUIRES_LOWER", "Owner password must contain at least one lowercase letter.");
        }

        var slug = (command.Slug ?? string.Empty).Trim().ToLowerInvariant();

        if (!TenantSlugValidator.IsValid(slug))
        {
            return Error.Validation(nameof(command.Slug), "INVALID_SLUG", "The organization subdomain slug is invalid.");
        }

        if (options.Value.ReservedSubdomains.Contains(slug, StringComparer.OrdinalIgnoreCase) ||
            await db.Tenants.AsNoTracking().AnyAsync(t => t.Slug == slug, ct))
        {
            return Error.Conflict("SLUG_ALREADY_TAKEN", "This organization subdomain is already taken or reserved.");
        }

        var name = (command.Name ?? string.Empty).Trim();
        var ownerEmail = (command.OwnerEmail ?? string.Empty).Trim();
        var now = timeProvider.GetUtcNow();

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            Name = name,
            Status = TenantStatus.Provisioning,
            OwnerEmail = ownerEmail,
            EncryptedOwnerPassword = protector.Protect(command.OwnerPassword),
            CreatedAt = now,
            UpdatedAt = now
        };

        db.Tenants.Add(tenant);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Error.Conflict("SLUG_ALREADY_TAKEN", "This organization subdomain is already taken or reserved.");
        }

        await cacheInvalidator.InvalidateTenantAsync(tenant.Id, tenant.Slug, ct);
        await bus.SendAsync(new StartTenantProvisioningCommand(tenant.Id));

        return new TenantProvisioningAcceptedResult(
            tenant.Id,
            tenant.Slug,
            tenant.Name,
            tenant.Status,
            tenant.CreatedAt);
    }
}
