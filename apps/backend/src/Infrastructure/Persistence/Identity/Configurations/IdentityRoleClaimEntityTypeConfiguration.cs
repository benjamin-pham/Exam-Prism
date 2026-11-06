namespace ExamPrism.Infrastructure.Persistence.Identity.Configurations;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class IdentityRoleClaimEntityTypeConfiguration : IEntityTypeConfiguration<IdentityRoleClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<Guid>> builder)
    {
        builder.ToTable("role_claims");

        builder.Property(rc => rc.Id).HasColumnName("id");
        builder.Property(rc => rc.RoleId).HasColumnName("role_id");
        builder.Property(rc => rc.ClaimType).HasColumnName("claim_type");
        builder.Property(rc => rc.ClaimValue).HasColumnName("claim_value");

        builder.HasIndex(rc => rc.RoleId).HasDatabaseName("ix_role_claims_role_id");
    }
}
