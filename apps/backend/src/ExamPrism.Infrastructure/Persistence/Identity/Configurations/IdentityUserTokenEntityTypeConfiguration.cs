namespace ExamPrism.Infrastructure.Persistence.Identity.Configurations;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class IdentityUserTokenEntityTypeConfiguration : IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder)
    {
        builder.ToTable("user_tokens");

        builder.Property(ut => ut.UserId).HasColumnName("user_id");
        builder.Property(ut => ut.LoginProvider).HasColumnName("login_provider");
        builder.Property(ut => ut.Name).HasColumnName("name");
        builder.Property(ut => ut.Value).HasColumnName("value");
    }
}
