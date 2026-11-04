namespace ExamPrism.Infrastructure.Persistence;

using System;
using System.Linq;
using System.Threading.Tasks;
using ExamPrism.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

public sealed class ReadOnlyAppDbContext : IDisposable, IAsyncDisposable
{
    private readonly AppDbContext _inner;

    public ReadOnlyAppDbContext(AppDbContext inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        _inner.ChangeTracker.AutoDetectChangesEnabled = false;
    }

    public AppDbContext DatabaseContext => _inner;
    public AppDbContext Context => _inner;
    public AppDbContext Inner => _inner;
    public ChangeTracker ChangeTracker => _inner.ChangeTracker;

    public IQueryable<ApplicationUser> Users => _inner.Users.AsNoTracking();
    public IQueryable<RefreshToken> RefreshTokens => _inner.RefreshTokens.AsNoTracking();

    public IQueryable<TEntity> Set<TEntity>() where TEntity : class => _inner.Set<TEntity>().AsNoTracking();

    public void Dispose() => _inner.Dispose();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
