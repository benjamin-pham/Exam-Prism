namespace ExamPrism.Infrastructure.Persistence.App.Configurations;

using ExamPrism.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class RefreshTokenEntityTypeConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(rt => rt.Id);

        builder.Property(rt => rt.Id)
            .HasColumnName("id");

        builder.Property(rt => rt.UserId)
            .HasColumnName("user_id");

        builder.Property(rt => rt.TokenHash)
            .HasColumnName("token_hash")
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(rt => rt.ExpiresAt)
            .HasColumnName("expires_at");

        builder.Property(rt => rt.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(rt => rt.RevokedAt)
            .HasColumnName("revoked_at");

        builder.Property(rt => rt.ReplacedByTokenHash)
            .HasColumnName("replaced_by_token_hash");

        builder.HasIndex(rt => rt.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_refresh_tokens_token_hash");

        builder.HasIndex(rt => rt.UserId)
            .HasDatabaseName("ix_refresh_tokens_user_id");

        builder.HasOne(rt => rt.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
