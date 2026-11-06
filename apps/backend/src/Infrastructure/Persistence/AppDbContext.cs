namespace ExamPrism.Infrastructure.Persistence;

using Common.Persistence;
using ExamPrism.Domain.Identity;
using ExamPrism.Infrastructure.Persistence.App.Configurations;
using ExamPrism.Infrastructure.Persistence.Identity.Configurations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options), IAppDbContext
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new RefreshTokenEntityTypeConfiguration());
        builder.ApplyConfiguration(new ApplicationUserEntityTypeConfiguration());
        builder.ApplyConfiguration(new TenantRoleEntityTypeConfiguration());
        builder.ApplyConfiguration(new IdentityUserRoleEntityTypeConfiguration());
        builder.ApplyConfiguration(new IdentityUserClaimEntityTypeConfiguration());
        builder.ApplyConfiguration(new IdentityUserLoginEntityTypeConfiguration());
        builder.ApplyConfiguration(new IdentityRoleClaimEntityTypeConfiguration());
        builder.ApplyConfiguration(new IdentityUserTokenEntityTypeConfiguration());
    }
}
