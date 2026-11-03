namespace ExamPrism.Infrastructure.Persistence.Catalog;

using ExamPrism.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : IdentityDbContext<PlatformUser, PlatformRole, Guid>(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<PlatformRefreshToken> PlatformRefreshTokens => Set<PlatformRefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Tenant>(b =>
        {
            b.ToTable("Tenants");
            b.HasKey(t => t.Id);

            b.Property(t => t.Slug)
                .IsRequired()
                .HasMaxLength(63);

            b.HasIndex(t => t.Slug)
                .IsUnique()
                .HasDatabaseName("IX_Tenants_Slug");

            b.Property(t => t.Name)
                .IsRequired()
                .HasMaxLength(200);

            b.Property(t => t.EncryptedConnectionString)
                .IsRequired(false);

            b.Property(t => t.Status)
                .HasConversion<string>()
                .HasMaxLength(32);

            b.HasIndex(t => t.Status)
                .HasDatabaseName("IX_Tenants_Status");

            b.Property(t => t.FailureReason)
                .HasMaxLength(1000)
                .IsRequired(false);

            b.Property(t => t.CreatedAt)
                .IsRequired();

            b.Property(t => t.ProvisionedAt)
                .IsRequired(false);

            b.Property(t => t.UpdatedAt)
                .IsRequired();
        });

        builder.Entity<PlatformRefreshToken>(b =>
        {
            b.ToTable("PlatformRefreshTokens");
            b.HasKey(rt => rt.Id);

            b.Property(rt => rt.TokenHash)
                .IsRequired()
                .HasMaxLength(128);

            b.HasIndex(rt => rt.TokenHash)
                .IsUnique()
                .HasDatabaseName("IX_PlatformRefreshTokens_TokenHash");

            b.HasIndex(rt => rt.UserId)
                .HasDatabaseName("IX_PlatformRefreshTokens_UserId");

            b.HasOne(rt => rt.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(rt => rt.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
