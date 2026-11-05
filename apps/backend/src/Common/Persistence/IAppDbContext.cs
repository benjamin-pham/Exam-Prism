namespace Common.Persistence;

using System.Threading;
using System.Threading.Tasks;
using ExamPrism.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

public interface IAppDbContext
{
    DbSet<RefreshToken> RefreshTokens { get; }
    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
