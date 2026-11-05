namespace ExamPrism.Infrastructure.Caching;

using System;
using System.Threading;
using System.Threading.Tasks;
using Common.Caching;
using Common.Telemetry;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

public sealed class RedisCacheBackplane : IHostedService, ICacheBackplane, IDisposable
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly HybridCache _hybridCache;
    private readonly CacheOptions _options;
    private readonly ILogger<RedisCacheBackplane> _logger;
    private readonly RedisChannel _channel;
    private bool _disposed;

    public string InstanceId { get; } = Guid.NewGuid().ToString("N");

    public event Action<string>? KeyInvalidated;

    public event Action<string>? TagInvalidated;

    public RedisCacheBackplane(
        IConnectionMultiplexer multiplexer,
        HybridCache hybridCache,
        IOptions<CacheOptions> options,
        ILogger<RedisCacheBackplane> logger)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _hybridCache = hybridCache ?? throw new ArgumentNullException(nameof(hybridCache));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _channel = RedisChannel.Literal($"{_options.KeyPrefix}{_options.BackplaneChannelName}");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var subscriber = _multiplexer.GetSubscriber();
            await subscriber.SubscribeAsync(_channel, OnMessageReceived);
            _logger.LogInformation("RedisCacheBackplane subscribed to channel {Channel} with instance ID {InstanceId}", (string)_channel!, InstanceId);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Failed to subscribe RedisCacheBackplane to channel {Channel}; continuing fail-open", (string)_channel!);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            var subscriber = _multiplexer.GetSubscriber();
            await subscriber.UnsubscribeAsync(_channel, OnMessageReceived);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Failed to unsubscribe RedisCacheBackplane from channel {Channel}", (string)_channel!);
        }
    }

    public async ValueTask PublishKeyInvalidationAsync(string scopedKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopedKey);

        try
        {
            var subscriber = _multiplexer.GetSubscriber();
            var payload = $"{InstanceId}|K|{scopedKey}";
            await subscriber.PublishAsync(_channel, payload);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Failed to publish key invalidation for {Key} to Redis backplane channel {Channel}", scopedKey, (string)_channel!);
        }
    }

    public async ValueTask PublishTagInvalidationAsync(string scopedTag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopedTag);

        try
        {
            var subscriber = _multiplexer.GetSubscriber();
            var payload = $"{InstanceId}|T|{scopedTag}";
            await subscriber.PublishAsync(_channel, payload);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Failed to publish tag invalidation for {Tag} to Redis backplane channel {Channel}", scopedTag, (string)_channel!);
        }
    }

    private void OnMessageReceived(RedisChannel channel, RedisValue message)
    {
        if (!message.HasValue)
        {
            return;
        }

        var raw = (string?)message;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var parts = raw.Split('|', 3);
        if (parts.Length < 3)
        {
            return;
        }

        var senderInstanceId = parts[0];
        var kind = parts[1];
        var target = parts[2];

        if (string.Equals(senderInstanceId, InstanceId, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            if (string.Equals(kind, "K", StringComparison.Ordinal))
            {
                var vt = _hybridCache.RemoveAsync(target);
                if (!vt.IsCompleted)
                {
                    vt.AsTask().GetAwaiter().GetResult();
                }
                ExamPrismDiagnostics.RecordCacheBackplaneEviction();
                NotifyListeners(KeyInvalidated, target, kind);
            }
            else if (string.Equals(kind, "T", StringComparison.Ordinal))
            {
                var vt = _hybridCache.RemoveByTagAsync(target);
                if (!vt.IsCompleted)
                {
                    vt.AsTask().GetAwaiter().GetResult();
                }
                ExamPrismDiagnostics.RecordCacheBackplaneEviction();
                NotifyListeners(TagInvalidated, target, kind);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to evict L1 cache entry {Target} ({Kind}) from backplane message", target, kind);
        }
    }

    private void NotifyListeners(Action<string>? handlers, string target, string kind)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var invocation in handlers.GetInvocationList())
        {
            if (invocation is Action<string> action)
            {
                try
                {
                    action(target);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Subscriber threw exception during cache backplane notification for {Target} ({Kind})", target, kind);
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            var subscriber = _multiplexer.GetSubscriber();
            subscriber.Unsubscribe(_channel, OnMessageReceived);
        }
        catch
        {
            // Suppress errors during disposal
        }
    }
}
