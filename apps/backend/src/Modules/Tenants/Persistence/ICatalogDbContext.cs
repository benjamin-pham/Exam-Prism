namespace Common.Persistence;

using System.Threading;
using System.Threading.Tasks;
using ExamPrism.Domain.Identity;
using ExamPrism.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

public interface ICatalogDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<PlatformRefreshToken> PlatformRefreshTokens { get; }
    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
