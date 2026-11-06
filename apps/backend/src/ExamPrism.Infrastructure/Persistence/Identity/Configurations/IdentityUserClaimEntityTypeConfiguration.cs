namespace ExamPrism.Infrastructure.Persistence.Identity.Configurations;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class IdentityUserClaimEntityTypeConfiguration : IEntityTypeConfiguration<IdentityUserClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder)
    {
        builder.ToTable("user_claims");

        builder.Property(uc => uc.Id).HasColumnName("id");
        builder.Property(uc => uc.UserId).HasColumnName("user_id");
        builder.Property(uc => uc.ClaimType).HasColumnName("claim_type");
        builder.Property(uc => uc.ClaimValue).HasColumnName("claim_value");

        builder.HasIndex(uc => uc.UserId).HasDatabaseName("ix_user_claims_user_id");
    }
}
