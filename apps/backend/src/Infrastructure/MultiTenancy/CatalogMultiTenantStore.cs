namespace ExamPrism.Infrastructure.MultiTenancy;

using Common.Caching;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.Persistence.Catalog;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

public sealed record CatalogTenantCacheEntry(
    bool Exists,
    Guid Id,
    string Slug,
    string Name,
    TenantStatus Status,
    string? EncryptedConnectionString);

public class CatalogMultiTenantStore : IMultiTenantStore<ExamPrismTenantInfo>, ITenantCacheInvalidator
{
    private static readonly SemaphoreSlim[] KeyStripes = Enumerable.Range(0, 256)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    private readonly CatalogDbContext _db;
    private readonly HybridCache _hybridCache;
    private readonly ICacheBackplane _backplane;
    private readonly IConnectionStringProtector _protector;
    private readonly MultiTenancyOptions _options;

    public CatalogMultiTenantStore(
        CatalogDbContext db,
        HybridCache hybridCache,
        IConnectionStringProtector protector,
        IOptions<MultiTenancyOptions> options,
        ICacheBackplane? backplane = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _hybridCache = hybridCache ?? throw new ArgumentNullException(nameof(hybridCache));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _backplane = backplane ?? new NoOpCacheBackplane();
    }

    private static SemaphoreSlim GetStripe(string key)
    {
        var hash = (uint)key.GetHashCode();
        return KeyStripes[hash % (uint)KeyStripes.Length];
    }

    private static TimeSpan ComputePositiveTtl(int minutes)
    {
        if (minutes <= 1)
        {
            return TimeSpan.FromMinutes(minutes);
        }

        // ±10% jitter: range [0.90, 1.10]
        var jitterFactor = 0.90 + (Random.Shared.NextDouble() * 0.20);
        return TimeSpan.FromMinutes(minutes * jitterFactor);
    }

    public async Task<ExamPrismTenantInfo?> GetByIdentifierAsync(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var slug = identifier.Trim().ToLowerInvariant();
        var key = $"catalog:tenant:slug:{slug}";

        var cached = await GetCacheEntryOrDefaultAsync(key);
        if (cached is not null)
        {
            return MapToTenantInfo(cached);
        }

        var stripe = GetStripe(key);
        await stripe.WaitAsync();
        try
        {
            cached = await GetCacheEntryOrDefaultAsync(key);
            if (cached is not null)
            {
                return MapToTenantInfo(cached);
            }

            var row = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug);
            if (row is null)
            {
                var notFoundEntry = new CatalogTenantCacheEntry(
                    Exists: false,
                    Id: Guid.Empty,
                    Slug: slug,
                    Name: string.Empty,
                    Status: TenantStatus.Provisioning,
                    EncryptedConnectionString: null);

                var notFoundOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromSeconds(_options.NotFoundCacheExpirationSeconds),
                    LocalCacheExpiration = TimeSpan.FromSeconds(_options.NotFoundCacheExpirationSeconds)
                };

                await _hybridCache.SetAsync(
                    key,
                    notFoundEntry,
                    notFoundOptions,
                    [$"catalog:tenant:slug:{slug}"]);

                return null;
            }

            var foundEntry = new CatalogTenantCacheEntry(
                Exists: true,
                Id: row.Id,
                Slug: row.Slug,
                Name: row.Name,
                Status: row.Status,
                EncryptedConnectionString: row.EncryptedConnectionString);

            var existingOptions = new HybridCacheEntryOptions
            {
                Expiration = ComputePositiveTtl(_options.CatalogCacheExpirationMinutes),
                LocalCacheExpiration = ComputePositiveTtl(_options.CatalogLocalCacheExpirationMinutes)
            };
            var tags = new[] { "catalog:tenants", $"catalog:tenant:{row.Id:D}", $"catalog:tenant:slug:{row.Slug}" };

            await _hybridCache.SetAsync(key, foundEntry, existingOptions, tags);
            await _hybridCache.SetAsync($"catalog:tenant:id:{row.Id:D}", foundEntry, existingOptions, tags);

            return MapToTenantInfo(foundEntry);
        }
        finally
        {
            stripe.Release();
        }
    }

    public async Task<ExamPrismTenantInfo?> GetAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var guid))
        {
            return null;
        }

        var key = $"catalog:tenant:id:{guid:D}";

        var cached = await GetCacheEntryOrDefaultAsync(key);
        if (cached is not null)
        {
            return MapToTenantInfo(cached);
        }

        var stripe = GetStripe(key);
        await stripe.WaitAsync();
        try
        {
            cached = await GetCacheEntryOrDefaultAsync(key);
            if (cached is not null)
            {
                return MapToTenantInfo(cached);
            }

            var row = await _db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == guid);
            if (row is null)
            {
                var notFoundEntry = new CatalogTenantCacheEntry(
                    Exists: false,
                    Id: guid,
                    Slug: string.Empty,
                    Name: string.Empty,
                    Status: TenantStatus.Provisioning,
                    EncryptedConnectionString: null);

                var notFoundOptions = new HybridCacheEntryOptions
                {
                    Expiration = TimeSpan.FromSeconds(_options.NotFoundCacheExpirationSeconds),
                    LocalCacheExpiration = TimeSpan.FromSeconds(_options.NotFoundCacheExpirationSeconds)
                };

                await _hybridCache.SetAsync(
                    key,
                    notFoundEntry,
                    notFoundOptions,
                    [$"catalog:tenant:id:{guid:D}"]);

                return null;
            }

            var foundEntry = new CatalogTenantCacheEntry(
                Exists: true,
                Id: row.Id,
                Slug: row.Slug,
                Name: row.Name,
                Status: row.Status,
                EncryptedConnectionString: row.EncryptedConnectionString);

            var existingOptions = new HybridCacheEntryOptions
            {
                Expiration = ComputePositiveTtl(_options.CatalogCacheExpirationMinutes),
                LocalCacheExpiration = ComputePositiveTtl(_options.CatalogLocalCacheExpirationMinutes)
            };
            var tags = new[] { "catalog:tenants", $"catalog:tenant:{row.Id:D}", $"catalog:tenant:slug:{row.Slug}" };

            await _hybridCache.SetAsync(key, foundEntry, existingOptions, tags);
            await _hybridCache.SetAsync($"catalog:tenant:slug:{row.Slug}", foundEntry, existingOptions, tags);

            return MapToTenantInfo(foundEntry);
        }
        finally
        {
            stripe.Release();
        }
    }

    public async Task<IEnumerable<ExamPrismTenantInfo>> GetAllAsync()
    {
        var rows = await _db.Tenants.AsNoTracking().ToListAsync();
        return rows
            .Select(r => MapToTenantInfo(new CatalogTenantCacheEntry(
                true, r.Id, r.Slug, r.Name, r.Status, r.EncryptedConnectionString))!)
            .Where(t => t is not null)
            .ToList();
    }

    public async Task<IEnumerable<ExamPrismTenantInfo>> GetAllAsync(int take, int skip)
    {
        var rows = await _db.Tenants.AsNoTracking()
            .OrderBy(t => t.Slug)
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        return rows
            .Select(r => MapToTenantInfo(new CatalogTenantCacheEntry(
                true, r.Id, r.Slug, r.Name, r.Status, r.EncryptedConnectionString))!)
            .Where(t => t is not null)
            .ToList();
    }

    public async Task<bool> AddAsync(ExamPrismTenantInfo tenantInfo)
    {
        ArgumentNullException.ThrowIfNull(tenantInfo);

        var tenantId = tenantInfo.TenantGuid != Guid.Empty
            ? tenantInfo.TenantGuid
            : (Guid.TryParse(tenantInfo.Id, out var parsedGuid) ? parsedGuid : Guid.NewGuid());

        var slug = tenantInfo.Identifier?.Trim().ToLowerInvariant() ?? string.Empty;

        string? encryptedConnStr = null;
        if (!string.IsNullOrWhiteSpace(tenantInfo.ConnectionString))
        {
            encryptedConnStr = _protector.Protect(tenantInfo.ConnectionString);
        }

        var tenant = new Tenant
        {
            Id = tenantId,
            Slug = slug,
            Name = tenantInfo.Name ?? string.Empty,
            Status = tenantInfo.Status,
            EncryptedConnectionString = encryptedConnStr,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _db.Tenants.AddAsync(tenant);
        await _db.SaveChangesAsync();

        await InvalidateTenantAsync(tenant.Id, tenant.Slug);
        return true;
    }

    public async Task<bool> UpdateAsync(ExamPrismTenantInfo tenantInfo)
    {
        ArgumentNullException.ThrowIfNull(tenantInfo);

        var tenantId = tenantInfo.TenantGuid != Guid.Empty
            ? tenantInfo.TenantGuid
            : (Guid.TryParse(tenantInfo.Id, out var parsedGuid) ? parsedGuid : Guid.Empty);

        var slug = tenantInfo.Identifier?.Trim().ToLowerInvariant();

        var existing = await _db.Tenants.FirstOrDefaultAsync(t =>
            (tenantId != Guid.Empty && t.Id == tenantId) ||
            (!string.IsNullOrEmpty(slug) && t.Slug == slug));

        if (existing is null)
        {
            return false;
        }

        existing.Name = tenantInfo.Name ?? existing.Name;
        existing.Status = tenantInfo.Status;
        if (tenantInfo.ConnectionString is not null)
        {
            existing.EncryptedConnectionString = !string.IsNullOrWhiteSpace(tenantInfo.ConnectionString)
                ? _protector.Protect(tenantInfo.ConnectionString)
                : null;
        }
        existing.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
        await InvalidateTenantAsync(existing.Id, existing.Slug);
        return true;
    }

    public async Task<bool> RemoveAsync(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return false;
        }

        var norm = identifier.Trim().ToLowerInvariant();
        var isGuid = Guid.TryParse(norm, out var guid);

        var existing = await _db.Tenants.FirstOrDefaultAsync(t =>
            t.Slug == norm || (isGuid && t.Id == guid));

        if (existing is null)
        {
            return false;
        }

        _db.Tenants.Remove(existing);
        await _db.SaveChangesAsync();
        await InvalidateTenantAsync(existing.Id, existing.Slug);
        return true;
    }

    public async ValueTask InvalidateTenantAsync(Guid tenantId, string slug, CancellationToken ct = default)
    {
        var keys = new List<string>(2);
        var tags = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(slug))
        {
            var normSlug = slug.Trim().ToLowerInvariant();
            keys.Add($"catalog:tenant:slug:{normSlug}");
            tags.Add($"catalog:tenant:slug:{normSlug}");
        }

        if (tenantId != Guid.Empty)
        {
            keys.Add($"catalog:tenant:id:{tenantId:D}");
            tags.Add($"catalog:tenant:{tenantId:D}");
            tags.Add($"t:{tenantId:D}");
        }

        foreach (var key in keys)
        {
            await _hybridCache.RemoveAsync(key, ct);
            await _backplane.PublishKeyInvalidationAsync(key, ct);
        }

        foreach (var tag in tags)
        {
            await _hybridCache.RemoveByTagAsync(tag, ct);
            await _backplane.PublishTagInvalidationAsync(tag, ct);
        }
    }

    private async ValueTask<CatalogTenantCacheEntry?> GetCacheEntryOrDefaultAsync(
        string key,
        CancellationToken ct = default)
    {
        var options = new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableUnderlyingData
        };

        return await _hybridCache.GetOrCreateAsync<object?, CatalogTenantCacheEntry?>(
            key,
            state: null,
            static (_, _) => ValueTask.FromResult<CatalogTenantCacheEntry?>(null),
            options,
            tags: null,
            cancellationToken: ct);
    }

    private ExamPrismTenantInfo? MapToTenantInfo(CatalogTenantCacheEntry entry)
    {
        if (!entry.Exists)
        {
            return null;
        }

        string? connectionString = null;
        if (entry.Status == TenantStatus.Active && !string.IsNullOrWhiteSpace(entry.EncryptedConnectionString))
        {
            try
            {
                connectionString = _protector.Unprotect(entry.EncryptedConnectionString);
            }
            catch
            {
                connectionString = null;
            }
        }

        return new ExamPrismTenantInfo(entry.Id.ToString("D"), entry.Slug)
        {
            TenantGuid = entry.Id,
            Name = entry.Name,
            Status = entry.Status,
            ConnectionString = connectionString
        };
    }
}
