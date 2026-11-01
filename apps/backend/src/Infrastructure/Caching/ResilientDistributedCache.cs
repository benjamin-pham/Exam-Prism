namespace ExamPrism.Infrastructure.Caching;

using System.Buffers;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

public sealed class ResilientDistributedCache : IBufferDistributedCache
{
    private readonly IDistributedCache _inner;
    private readonly IBufferDistributedCache? _bufferInner;
    private readonly ILogger<ResilientDistributedCache> _logger;

    public ResilientDistributedCache(IDistributedCache inner, ILogger<ResilientDistributedCache> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _bufferInner = inner as IBufferDistributedCache;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public byte[]? Get(string key)
    {
        try
        {
            return _inner.Get(key);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation Get failed for key {CacheKey}; failing open.", key);
            return null;
        }
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        try
        {
            return await _inner.GetAsync(key, token);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation GetAsync failed for key {CacheKey}; failing open.", key);
            return null;
        }
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
    {
        try
        {
            _inner.Set(key, value, options);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation Set failed for key {CacheKey}; failing open.", key);
        }
    }

    public async Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        try
        {
            await _inner.SetAsync(key, value, options, token);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation SetAsync failed for key {CacheKey}; failing open.", key);
        }
    }

    public void Refresh(string key)
    {
        try
        {
            _inner.Refresh(key);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation Refresh failed for key {CacheKey}; failing open.", key);
        }
    }

    public async Task RefreshAsync(string key, CancellationToken token = default)
    {
        try
        {
            await _inner.RefreshAsync(key, token);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation RefreshAsync failed for key {CacheKey}; failing open.", key);
        }
    }

    public void Remove(string key)
    {
        try
        {
            _inner.Remove(key);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation Remove failed for key {CacheKey}; failing open.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken token = default)
    {
        try
        {
            await _inner.RemoveAsync(key, token);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation RemoveAsync failed for key {CacheKey}; failing open.", key);
        }
    }

    public bool TryGet(string key, IBufferWriter<byte> destination)
    {
        try
        {
            if (_bufferInner != null)
            {
                return _bufferInner.TryGet(key, destination);
            }

            var bytes = _inner.Get(key);
            if (bytes is null)
            {
                return false;
            }

            destination.Write(bytes);
            return true;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation TryGet failed for key {CacheKey}; failing open.", key);
            return false;
        }
    }

    public async ValueTask<bool> TryGetAsync(string key, IBufferWriter<byte> destination, CancellationToken token = default)
    {
        try
        {
            if (_bufferInner != null)
            {
                return await _bufferInner.TryGetAsync(key, destination, token);
            }

            var bytes = await _inner.GetAsync(key, token);
            if (bytes is null)
            {
                return false;
            }

            destination.Write(bytes);
            return true;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation TryGetAsync failed for key {CacheKey}; failing open.", key);
            return false;
        }
    }

    public void Set(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options)
    {
        try
        {
            if (_bufferInner != null)
            {
                _bufferInner.Set(key, value, options);
                return;
            }

            _inner.Set(key, value.ToArray(), options);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation buffer Set failed for key {CacheKey}; failing open.", key);
        }
    }

    public async ValueTask SetAsync(string key, ReadOnlySequence<byte> value, DistributedCacheEntryOptions options, CancellationToken token = default)
    {
        try
        {
            if (_bufferInner != null)
            {
                await _bufferInner.SetAsync(key, value, options, token);
                return;
            }

            await _inner.SetAsync(key, value.ToArray(), options, token);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Redis L2 cache operation buffer SetAsync failed for key {CacheKey}; failing open.", key);
        }
    }
}
