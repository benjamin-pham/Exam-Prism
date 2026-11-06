namespace ExamPrism.Infrastructure.Http;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Common.Caching;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

public sealed class RedisOutputCacheStore : IOutputCacheStore, IDisposable
{
    private const string EvictByTagLuaScript = @"
local tagKey = KEYS[1]
local members = redis.call('SMEMBERS', tagKey)
for i = 1, #members do
    redis.call('DEL', members[i])
end
redis.call('DEL', tagKey)
return #members";

    private readonly IConnectionMultiplexer? _connectionMultiplexer;
    private readonly IOutputCacheStore _fallbackStore;
    private readonly ILogger<RedisOutputCacheStore>? _logger;
    private readonly ICacheBackplane? _backplane;
    private readonly string _keyPrefix;

    public RedisOutputCacheStore(
        IConnectionMultiplexer? connectionMultiplexer = null,
        IOutputCacheStore? fallbackStore = null,
        ILogger<RedisOutputCacheStore>? logger = null,
        ICacheBackplane? backplane = null,
        string? keyPrefix = null)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _fallbackStore = fallbackStore ?? new MemoryOutputCacheFallbackStore();
        _logger = logger;
        _backplane = backplane;
        _keyPrefix = keyPrefix ?? string.Empty;

        if (_backplane is not null)
        {
            _backplane.TagInvalidated += OnBackplaneTagInvalidated;
        }
    }

    private string FormatKey(string key) => string.IsNullOrEmpty(_keyPrefix) ? key : $"{_keyPrefix}{key}";

    private string FormatTagKey(string tag) => string.IsNullOrEmpty(_keyPrefix)
        ? $"outputcache:tag:{tag}"
        : $"{_keyPrefix}outputcache:tag:{tag}";

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_connectionMultiplexer is null || !_connectionMultiplexer.IsConnected)
        {
            return await _fallbackStore.GetAsync(key, cancellationToken);
        }

        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var redisKey = FormatKey(key);
            var value = await db.StringGetAsync(redisKey);
            if (!value.HasValue)
            {
                return null;
            }

            return (byte[]?)value;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or SocketException)
        {
            _logger?.LogWarning(ex, "Redis error during GetAsync for key {Key}. Failing open to fallback store.", key);
            return await _fallbackStore.GetAsync(key, cancellationToken);
        }
    }

    public async ValueTask SetAsync(
        string key,
        byte[] value,
        string[]? tags,
        TimeSpan validFor,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (_connectionMultiplexer is null || !_connectionMultiplexer.IsConnected)
        {
            await _fallbackStore.SetAsync(key, value, tags, validFor, cancellationToken);
            return;
        }

        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            var redisKey = FormatKey(key);
            await db.StringSetAsync(redisKey, value, validFor);

            if (tags is { Length: > 0 })
            {
                var tagTasks = new List<Task>();
                foreach (var tag in tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        var tagKey = FormatTagKey(tag);
                        tagTasks.Add(AddTagKeyAsync(db, tagKey, redisKey, validFor));
                    }
                }

                if (tagTasks.Count > 0)
                {
                    await Task.WhenAll(tagTasks);
                }
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or SocketException)
        {
            _logger?.LogWarning(ex, "Redis error during SetAsync for key {Key}. Failing open to fallback store.", key);
            await _fallbackStore.SetAsync(key, value, tags, validFor, cancellationToken);
        }
    }

    private static async Task AddTagKeyAsync(IDatabase db, string tagKey, string key, TimeSpan validFor)
    {
        await db.SetAddAsync(tagKey, key);
        await db.KeyExpireAsync(tagKey, validFor);
    }

    public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var tagKey = FormatTagKey(tag);

        if (_connectionMultiplexer is not null && _connectionMultiplexer.IsConnected)
        {
            try
            {
                var db = _connectionMultiplexer.GetDatabase();
                await db.ScriptEvaluateAsync(
                    EvictByTagLuaScript,
                    keys: [(RedisKey)tagKey]);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException or SocketException)
            {
                _logger?.LogWarning(ex, "Redis error during EvictByTagAsync for tag {Tag}. Falling back to fallback store.", tag);
            }
        }

        await _fallbackStore.EvictByTagAsync(tag, cancellationToken);

        if (_backplane is not null)
        {
            try
            {
                await _backplane.PublishTagInvalidationAsync($"outputcache:tag:{tag}", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to publish output cache tag invalidation for {Tag}", tag);
            }
        }
    }

    private void OnBackplaneTagInvalidated(string tag)
    {
        const string prefix = "outputcache:tag:";
        var actualTag = tag.StartsWith(prefix, StringComparison.Ordinal)
            ? tag[prefix.Length..]
            : tag;

        try
        {
            var vt = _fallbackStore.EvictByTagAsync(actualTag, CancellationToken.None);
            if (!vt.IsCompleted)
            {
                vt.AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error evicting fallback store for backplane tag {Tag}", actualTag);
        }
    }

    public void Dispose()
    {
        if (_backplane is not null)
        {
            _backplane.TagInvalidated -= OnBackplaneTagInvalidated;
        }
    }

    internal sealed class MemoryOutputCacheFallbackStore : IOutputCacheStore
    {
        private sealed record Entry(byte[] Value, DateTime ExpiresAt, HashSet<string>? Tags);

        private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _tagToKeys = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > DateTime.UtcNow)
                {
                    return ValueTask.FromResult<byte[]?>(entry.Value);
                }

                _entries.TryRemove(key, out _);
            }

            return ValueTask.FromResult<byte[]?>(null);
        }

        public ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
        {
            var expiresAt = DateTime.UtcNow.Add(validFor);
            HashSet<string>? tagSet = null;

            if (tags is { Length: > 0 })
            {
                tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var tag in tags)
                {
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        tagSet.Add(tag);
                        var keys = _tagToKeys.GetOrAdd(tag, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                        keys[key] = 0;
                    }
                }
            }

            _entries[key] = new Entry(value, expiresAt, tagSet);
            return ValueTask.CompletedTask;
        }

        public ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
        {
            if (_tagToKeys.TryRemove(tag, out var keys))
            {
                foreach (var key in keys.Keys)
                {
                    _entries.TryRemove(key, out _);
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
