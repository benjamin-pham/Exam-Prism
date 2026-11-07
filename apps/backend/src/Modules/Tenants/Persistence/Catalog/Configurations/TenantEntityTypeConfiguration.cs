namespace ExamPrism.Infrastructure.Persistence.Catalog.Configurations;

using ExamPrism.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TenantEntityTypeConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("tenants");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Id)
            .HasColumnName("id");

        builder.Property(t => t.Slug)
            .HasColumnName("slug")
            .IsRequired()
            .HasMaxLength(63);

        builder.HasIndex(t => t.Slug)
            .IsUnique()
            .HasDatabaseName("ix_tenants_slug");

        builder.Property(t => t.Name)
            .HasColumnName("name")
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(t => t.EncryptedConnectionString)
            .HasColumnName("encrypted_connection_string")
            .IsRequired(false);

        builder.Property(t => t.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.HasIndex(t => t.Status)
            .HasDatabaseName("ix_tenants_status");

        builder.Property(t => t.FailureReason)
            .HasColumnName("failure_reason")
            .HasMaxLength(1000)
            .IsRequired(false);

        builder.Property(t => t.OwnerEmail)
            .HasColumnName("owner_email")
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(t => t.EncryptedOwnerPassword)
            .HasColumnName("encrypted_owner_password")
            .IsRequired(false);

        builder.Property(t => t.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(t => t.ProvisionedAt)
            .HasColumnName("provisioned_at")
            .IsRequired(false);

        builder.Property(t => t.UpdatedAt)
            .HasColumnName("updated_at")
            .IsRequired();

        builder.Ignore(t => t.DomainEvents);
    }
}
