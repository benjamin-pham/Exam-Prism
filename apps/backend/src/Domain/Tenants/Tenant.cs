namespace ExamPrism.Domain.Tenants;

using Common.Domain;
using Common.MultiTenancy;
using Common.Results;
using ExamPrism.Domain.Tenants.Events;

public class Tenant : AggregateRoot<Guid>, IAuditableEntity
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? EncryptedConnectionString { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Provisioning;

    public string? FailureReason { get; set; }

    public string? OwnerEmail { get; set; }

    public string? EncryptedOwnerPassword { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ProvisionedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Tenant()
    {
    }

    public static Result<Tenant> Create(
        string slug,
        string name,
        string ownerEmail,
        string encryptedOwnerPassword,
        DateTimeOffset now)
    {
        var slugResult = TenantSlug.Create(slug);
        if (slugResult.IsFailure)
        {
            return slugResult.Error;
        }

        var trimmedName = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            return TenantErrors.NameRequired();
        }

        if (trimmedName.Length > 200)
        {
            return TenantErrors.NameTooLong();
        }

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Slug = slugResult.Value.Value,
            Name = trimmedName,
            Status = TenantStatus.Provisioning,
            OwnerEmail = (ownerEmail ?? string.Empty).Trim(),
            EncryptedOwnerPassword = encryptedOwnerPassword,
            CreatedAt = now,
            UpdatedAt = now
        };

        tenant.RaiseDomainEvent(new TenantCreatedDomainEvent(tenant.Id, tenant.Slug, now));
        return tenant;
    }

    public Result Rename(string newName, DateTimeOffset now)
    {
        var trimmed = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return TenantErrors.NameRequired();
        }

        if (trimmed.Length > 200)
        {
            return TenantErrors.NameTooLong();
        }

        var oldName = Name;
        Name = trimmed;
        UpdatedAt = now;

        RaiseDomainEvent(new TenantRenamedDomainEvent(Id, oldName, Name, now));
        return Result.Success();
    }

    public Result Suspend(DateTimeOffset now)
    {
        if (Status != TenantStatus.Active)
        {
            return TenantErrors.InvalidStatusTransition("Only an active organization workspace can be suspended.");
        }

        Status = TenantStatus.Suspended;
        UpdatedAt = now;

        RaiseDomainEvent(new TenantSuspendedDomainEvent(Id, Slug, now));
        return Result.Success();
    }

    public Result Reactivate(DateTimeOffset now)
    {
        if (Status != TenantStatus.Suspended || string.IsNullOrWhiteSpace(EncryptedConnectionString))
        {
            return TenantErrors.InvalidStatusTransition("Only a suspended organization workspace with a valid database connection can be reactivated.");
        }

        Status = TenantStatus.Active;
        UpdatedAt = now;

        RaiseDomainEvent(new TenantReactivatedDomainEvent(Id, Slug, now));
        return Result.Success();
    }

    public Result RetryProvisioning(DateTimeOffset now)
    {
        if (Status is TenantStatus.Active or TenantStatus.Suspended)
        {
            return TenantErrors.InvalidStatusTransition("Only a failed or provisioning organization workspace can retry provisioning.");
        }

        Status = TenantStatus.Provisioning;
        FailureReason = null;
        UpdatedAt = now;

        RaiseDomainEvent(new TenantProvisioningRetriedDomainEvent(Id, Slug, now));
        return Result.Success();
    }

    public void SetEncryptedConnectionString(string encryptedConnectionString, DateTimeOffset now)
    {
        EncryptedConnectionString = encryptedConnectionString;
        UpdatedAt = now;
    }

    public void MarkProvisioned(string encryptedConnectionString, DateTimeOffset now)
    {
        EncryptedConnectionString = encryptedConnectionString;
        Status = TenantStatus.Active;
        FailureReason = null;
        EncryptedOwnerPassword = null;
        ProvisionedAt = now;
        UpdatedAt = now;

        RaiseDomainEvent(new TenantProvisionedDomainEvent(Id, Slug, now));
    }

    public void MarkProvisioningFailed(string failureReason, DateTimeOffset now)
    {
        Status = TenantStatus.Failed;
        FailureReason = failureReason.Length > 1000 ? failureReason[..1000] : failureReason;
        UpdatedAt = now;

        RaiseDomainEvent(new TenantProvisioningFailedDomainEvent(Id, Slug, FailureReason, now));
    }

    public void SetCreated(DateTimeOffset now) => CreatedAt = now;
    public void SetUpdated(DateTimeOffset now) => UpdatedAt = now;
}

