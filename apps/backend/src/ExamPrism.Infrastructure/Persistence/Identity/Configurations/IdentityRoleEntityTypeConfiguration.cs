namespace ExamPrism.Infrastructure.Persistence.Identity.Configurations;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public class IdentityRoleEntityTypeConfiguration<TRole> : IEntityTypeConfiguration<TRole>
    where TRole : IdentityRole<Guid>
{
    public virtual void Configure(EntityTypeBuilder<TRole> builder)
    {
        builder.ToTable("roles");

        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Name).HasColumnName("name");
        builder.Property(r => r.NormalizedName).HasColumnName("normalized_name");
        builder.Property(r => r.ConcurrencyStamp).HasColumnName("concurrency_stamp");

        builder.HasIndex(r => r.NormalizedName).IsUnique().HasDatabaseName("ix_roles_normalized_name");
    }
}
