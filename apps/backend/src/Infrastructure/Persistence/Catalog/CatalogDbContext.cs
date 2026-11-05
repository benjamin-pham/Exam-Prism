namespace ExamPrism.Infrastructure.Persistence.Catalog;

using ExamPrism.Domain.Tenants;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence.Catalog.Configurations;
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

        builder.ApplyConfiguration(new TenantEntityTypeConfiguration());

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
