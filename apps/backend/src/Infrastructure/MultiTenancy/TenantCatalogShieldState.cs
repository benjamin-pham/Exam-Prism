namespace ExamPrism.Infrastructure.MultiTenancy;

using System;
using System.Collections.Concurrent;
using System.Threading;
using Common.Caching;
using Microsoft.Extensions.Options;

public sealed class TenantCatalogShieldState
{
    private readonly MultiTenancyOptions _options;
    private readonly ICacheBackplane _backplane;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, long> _negativeCache = new(StringComparer.OrdinalIgnoreCase);
    private long _missWindowSecond;
    private int _missCountInSecond;

    public TenantCatalogShieldState(
        IOptions<MultiTenancyOptions> options,
        ICacheBackplane? backplane = null,
        TimeProvider? timeProvider = null)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _backplane = backplane ?? new NoOpCacheBackplane();
        _timeProvider = timeProvider ?? TimeProvider.System;

        _backplane.KeyInvalidated += OnBackplaneKeyInvalidated;
        _backplane.TagInvalidated += OnBackplaneTagInvalidated;
    }

    private void OnBackplaneKeyInvalidated(string key)
    {
        if (key.StartsWith("catalog:tenant:", StringComparison.OrdinalIgnoreCase))
        {
            RemoveNegativeCache(key);
        }
    }

    private void OnBackplaneTagInvalidated(string tag)
    {
        if (tag.StartsWith("catalog:tenant:slug:", StringComparison.OrdinalIgnoreCase))
        {
            var slug = tag["catalog:tenant:slug:".Length..];
            RemoveNegativeCache($"catalog:tenant:slug:{slug}");
        }
    }

    public bool IsNegativelyCached(string key)
    {
        if (_negativeCache.TryGetValue(key, out var expiresAt))
        {
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (nowMs < expiresAt)
            {
                return true;
            }

            _negativeCache.TryRemove(key, out _);
        }

        return false;
    }

    public void RecordNegativeCache(string key, TimeSpan ttl)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var expiresAt = nowMs + Math.Max(1000L, (long)ttl.TotalMilliseconds);
        _negativeCache[key] = expiresAt;

        if (_negativeCache.Count > _options.MaxNegativeCacheEntries)
        {
            foreach (var kvp in _negativeCache)
            {
                if (nowMs >= kvp.Value)
                {
                    _negativeCache.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    public void RemoveNegativeCache(string key)
    {
        _negativeCache.TryRemove(key, out _);
    }

    public bool TryAcquireCatalogDbMissPermit()
    {
        if (_options.MaxCatalogDbMissesPerSecond <= 0)
        {
            return true;
        }

        var currentSecond = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var windowSec = Volatile.Read(ref _missWindowSecond);
        if (windowSec != currentSecond && Interlocked.CompareExchange(ref _missWindowSecond, currentSecond, windowSec) == windowSec)
        {
            Interlocked.Exchange(ref _missCountInSecond, 0);
        }

        var count = Interlocked.Increment(ref _missCountInSecond);
        return count <= _options.MaxCatalogDbMissesPerSecond;
    }
}

