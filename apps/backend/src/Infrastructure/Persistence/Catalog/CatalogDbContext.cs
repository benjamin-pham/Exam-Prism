namespace ExamPrism.Infrastructure.Persistence.Catalog;

using ExamPrism.Domain.Tenants;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence.Catalog.Configurations;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : IdentityDbContext<PlatformUser, PlatformRole, Guid>(options)
{
    static CatalogDbContext()
    {
        DapperTypeHandlers.EnsureRegistered();
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<PlatformRefreshToken> PlatformRefreshTokens => Set<PlatformRefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new TenantEntityTypeConfiguration());
        builder.ApplyConfiguration(new PlatformRefreshTokenEntityTypeConfiguration());
    }
}

