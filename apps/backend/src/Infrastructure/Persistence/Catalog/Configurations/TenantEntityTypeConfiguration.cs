namespace ExamPrism.Infrastructure.Persistence.Catalog.Configurations;

using ExamPrism.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class TenantEntityTypeConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Slug)
            .IsRequired()
            .HasMaxLength(63);

        builder.HasIndex(t => t.Slug)
            .IsUnique()
            .HasDatabaseName("IX_Tenants_Slug");

        builder.Property(t => t.Name)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(t => t.EncryptedConnectionString)
            .IsRequired(false);

        builder.Property(t => t.Status)
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.HasIndex(t => t.Status)
            .HasDatabaseName("IX_Tenants_Status");

        builder.Property(t => t.FailureReason)
            .HasMaxLength(1000)
            .IsRequired(false);

        builder.Property(t => t.OwnerEmail)
            .HasMaxLength(256)
            .IsRequired(false);

        builder.Property(t => t.EncryptedOwnerPassword)
            .IsRequired(false);

        builder.Property(t => t.CreatedAt)
            .IsRequired();

        builder.Property(t => t.ProvisionedAt)
            .IsRequired(false);

        builder.Property(t => t.UpdatedAt)
            .IsRequired();

        builder.Ignore(t => t.DomainEvents);
    }
}

