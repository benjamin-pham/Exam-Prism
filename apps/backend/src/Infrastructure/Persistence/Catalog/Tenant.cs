namespace ExamPrism.Infrastructure.Persistence.Catalog;

using Common.MultiTenancy;

public class Tenant
{
    public Guid Id { get; set; }

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
}
