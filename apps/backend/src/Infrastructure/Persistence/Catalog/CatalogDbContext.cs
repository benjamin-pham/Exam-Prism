namespace ExamPrism.Infrastructure.Persistence.Catalog;

using Microsoft.EntityFrameworkCore;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();

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
    }
}
