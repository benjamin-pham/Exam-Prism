namespace ExamPrism.Infrastructure.Persistence;

using System;
using System.Linq;
using System.Threading.Tasks;
using ExamPrism.Infrastructure.Identity;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

public sealed class ReadOnlyCatalogDbContext : IDisposable, IAsyncDisposable
{
    private readonly CatalogDbContext _inner;

    public ReadOnlyCatalogDbContext(CatalogDbContext inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inner.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        _inner.ChangeTracker.AutoDetectChangesEnabled = false;
    }

    public CatalogDbContext DatabaseContext => _inner;
    public CatalogDbContext Context => _inner;
    public CatalogDbContext Inner => _inner;
    public ChangeTracker ChangeTracker => _inner.ChangeTracker;

    public IQueryable<Tenant> Tenants => _inner.Tenants.AsNoTracking();
    public IQueryable<PlatformUser> Users => _inner.Users.AsNoTracking();
    public IQueryable<PlatformRefreshToken> PlatformRefreshTokens => _inner.PlatformRefreshTokens.AsNoTracking();

    public IQueryable<TEntity> Set<TEntity>() where TEntity : class => _inner.Set<TEntity>().AsNoTracking();

    public void Dispose() => _inner.Dispose();
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
