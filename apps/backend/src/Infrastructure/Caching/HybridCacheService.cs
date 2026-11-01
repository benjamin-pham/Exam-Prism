namespace ExamPrism.Infrastructure.Caching;

using Common.Caching;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

public sealed class HybridCacheService : ICacheService
{
    private readonly HybridCache _hybridCache;
    private readonly CacheOptions _options;

    public HybridCacheService(HybridCache hybridCache, IOptions<CacheOptions> options)
    {
        _hybridCache = hybridCache ?? throw new ArgumentNullException(nameof(hybridCache));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async ValueTask<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> factory,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        var options = CreateEntryOptions(expiration, localExpiration);
        var tagCollection = tags as IReadOnlyCollection<string> ?? tags?.ToList();

        return await _hybridCache.GetOrCreateAsync(
            key,
            factory,
            options,
            tagCollection,
            cancellationToken);
    }

    public async ValueTask<T?> GetOrDefaultAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var options = new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableUnderlyingData
        };

        return await _hybridCache.GetOrCreateAsync<object?, T?>(
            key,
            state: null,
            static (_, _) => ValueTask.FromResult<T?>(default),
            options,
            tags: null,
            cancellationToken);
    }

    public async ValueTask SetAsync<T>(
        string key,
        T value,
        TimeSpan? expiration = null,
        TimeSpan? localExpiration = null,
        IEnumerable<string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var options = CreateEntryOptions(expiration, localExpiration);
        var tagCollection = tags as IReadOnlyCollection<string> ?? tags?.ToList();

        await _hybridCache.SetAsync(
            key,
            value,
            options,
            tagCollection,
            cancellationToken);
    }

    public async ValueTask RemoveAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _hybridCache.RemoveAsync(key, cancellationToken);
    }

    public async ValueTask RemoveByTagAsync(
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        await _hybridCache.RemoveByTagAsync(tag, cancellationToken);
    }

    private HybridCacheEntryOptions? CreateEntryOptions(TimeSpan? expiration, TimeSpan? localExpiration)
    {
        if (expiration is null && localExpiration is null)
        {
            return null;
        }

        var effectiveExpiration = expiration ?? TimeSpan.FromMinutes(_options.DefaultExpirationMinutes);
        var defaultLocal = TimeSpan.FromMinutes(_options.DefaultLocalExpirationMinutes);
        var effectiveLocal = localExpiration ?? (effectiveExpiration < defaultLocal ? effectiveExpiration : defaultLocal);

        return new HybridCacheEntryOptions
        {
            Expiration = effectiveExpiration,
            LocalCacheExpiration = effectiveLocal
        };
    }
}
