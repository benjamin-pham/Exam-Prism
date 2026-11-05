namespace ExamPrism.Infrastructure.Security;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Common.Caching;
using Common.Security;
using Common.Telemetry;
using ExamPrism.Infrastructure.Caching;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

public sealed class HybridIpReputationService : IIpReputationService
{
    private sealed record StrikeState(long WindowStartUnixMs, int Score);

    private const string AtomicStrikeLuaScript = @"
local current = redis.call('INCRBY', KEYS[1], tonumber(ARGV[1]))
if current == tonumber(ARGV[1]) then
    redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[2]))
end
return current";

    private readonly IOptions<DdosProtectionOptions> _options;
    private readonly ICacheService? _cache;
    private readonly TimeProvider _timeProvider;
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly ICacheBackplane? _backplane;
    private readonly string _keyPrefix;

    private readonly ConcurrentDictionary<string, long> _localBans = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _checkedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StrikeState> _strikes = new(StringComparer.OrdinalIgnoreCase);

    public HybridIpReputationService(
        IOptions<DdosProtectionOptions> options,
        ICacheService? cache = null,
        TimeProvider? timeProvider = null)
        : this(options, cache, timeProvider, multiplexer: null, backplane: null, cacheOptions: null)
    {
    }

    public HybridIpReputationService(
        IOptions<DdosProtectionOptions> options,
        ICacheService? cache,
        TimeProvider? timeProvider,
        IConnectionMultiplexer? multiplexer,
        ICacheBackplane? backplane = null,
        IOptions<CacheOptions>? cacheOptions = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cache = cache;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _multiplexer = multiplexer;
        _backplane = backplane;
        _keyPrefix = cacheOptions?.Value.KeyPrefix ?? "examprism:";

        if (_backplane is not null)
        {
            _backplane.KeyInvalidated += OnBackplaneKeyInvalidated;
        }
    }

    private void OnBackplaneKeyInvalidated(string key)
    {
        if (key.StartsWith("ddos:ban:ip:", StringComparison.OrdinalIgnoreCase))
        {
            var ip = key["ddos:ban:ip:".Length..];
            _checkedIps.TryRemove(ip, out _);
            _localBans.TryRemove(ip, out _);
        }
    }

    public async ValueTask<TimeSpan?> GetBanRemainingAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        // 1. Check local in-memory bans
        if (_localBans.TryGetValue(ipAddress, out var expiresAt))
        {
            if (nowMs < expiresAt)
            {
                return TimeSpan.FromMilliseconds(expiresAt - nowMs);
            }

            _localBans.TryRemove(ipAddress, out _);
        }

        // 2. Check local in-memory checked unbanned IPs (0 Redis L2 calls on clean traffic)
        if (_checkedIps.TryGetValue(ipAddress, out var checkedUntil))
        {
            if (nowMs < checkedUntil)
            {
                return null;
            }

            _checkedIps.TryRemove(ipAddress, out _);
        }

        // 3. Fallback to shared Redis or ICacheService
        long? remoteExpiresAt = null;

        if (_multiplexer is { IsConnected: true })
        {
            try
            {
                var db = _multiplexer.GetDatabase();
                var redisVal = await db.StringGetAsync($"{_keyPrefix}ddos:ban:ip:{ipAddress}");
                if (redisVal.HasValue && redisVal.TryParse(out long parsedExp))
                {
                    remoteExpiresAt = parsedExp;
                }
            }
            catch
            {
                // fail open
            }
        }
        else if (_cache is not null)
        {
            var cacheKey = $"ddos:ban:ip:{ipAddress}";
            remoteExpiresAt = await _cache.GetOrDefaultAsync<long?>(cacheKey, cancellationToken);
        }

        if (remoteExpiresAt.HasValue)
        {
            if (nowMs < remoteExpiresAt.Value)
            {
                _localBans[ipAddress] = remoteExpiresAt.Value;
                _checkedIps.TryRemove(ipAddress, out _);
                return TimeSpan.FromMilliseconds(remoteExpiresAt.Value - nowMs);
            }

            if (_multiplexer is { IsConnected: true })
            {
                try
                {
                    var db = _multiplexer.GetDatabase();
                    await db.KeyDeleteAsync($"{_keyPrefix}ddos:ban:ip:{ipAddress}");
                }
                catch { }
            }
            else if (_cache is not null)
            {
                await _cache.RemoveAsync($"ddos:ban:ip:{ipAddress}", cancellationToken);
            }
        }

        // Cache as clean in L1 for the duration of StrikeWindowSeconds
        var cleanTtlMs = Math.Max(1000L, _options.Value.StrikeWindowSeconds * 1000L);
        _checkedIps[ipAddress] = nowMs + cleanTtlMs;
        PruneIfExceededCapacity();
        return null;
    }

    public async ValueTask RecordStrikeAsync(string ipAddress, IpStrikeReason reason, int customWeight = 0, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var opt = _options.Value;
        if (!opt.Enabled)
        {
            return;
        }

        var weight = customWeight > 0
            ? customWeight
            : reason switch
            {
                IpStrikeReason.DecompressionBomb => opt.DecompressionBombWeight,
                IpStrikeReason.UnknownTenantProbe => opt.UnknownTenantProbeWeight,
                IpStrikeReason.RateLimitExceeded => opt.RateLimitExceededWeight,
                IpStrikeReason.UnauthorizedAuthAttempt => opt.UnauthorizedAuthAttemptWeight,
                _ => 10
            };

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var windowMs = opt.StrikeWindowSeconds * 1000L;

        var newState = _strikes.AddOrUpdate(
            ipAddress,
            _ => new StrikeState(nowMs, weight),
            (_, existing) =>
            {
                if (nowMs - existing.WindowStartUnixMs > windowMs)
                {
                    return new StrikeState(nowMs, weight);
                }

                return new StrikeState(existing.WindowStartUnixMs, existing.Score + weight);
            });

        var effectiveScore = newState.Score;

        if (_multiplexer is { IsConnected: true })
        {
            try
            {
                var db = _multiplexer.GetDatabase();
                var redisResult = await db.ScriptEvaluateAsync(
                    AtomicStrikeLuaScript,
                    keys: [(RedisKey)$"{_keyPrefix}ddos:strike:ip:{ipAddress}"],
                    values: [(RedisValue)weight, (RedisValue)windowMs]);

                if (redisResult is not null)
                {
                    effectiveScore = Math.Max(effectiveScore, (int)(long)redisResult);
                }
            }
            catch
            {
                // fail open to in-memory score
            }
        }

        if (effectiveScore >= opt.BanThresholdScore)
        {
            _strikes.TryRemove(ipAddress, out _);
            await BanIpAsync(ipAddress, TimeSpan.FromSeconds(opt.BanDurationSeconds), cancellationToken);
        }

        PruneIfExceededCapacity();
    }

    public async ValueTask BanIpAsync(string ipAddress, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var expiresAt = nowMs + Math.Max(1000L, (long)duration.TotalMilliseconds);

        _localBans[ipAddress] = expiresAt;
        _checkedIps.TryRemove(ipAddress, out _);
        ExamPrismDiagnostics.RecordIpBanIssued();

        if (_multiplexer is { IsConnected: true })
        {
            try
            {
                var db = _multiplexer.GetDatabase();
                await db.StringSetAsync(
                    (RedisKey)$"{_keyPrefix}ddos:ban:ip:{ipAddress}",
                    (RedisValue)expiresAt,
                    duration,
                    keepTtl: false,
                    When.Always,
                    CommandFlags.None);
            }
            catch { }
        }

        if (_cache is not null)
        {
            await _cache.SetAsync($"ddos:ban:ip:{ipAddress}", expiresAt, duration, duration, tags: null, cancellationToken);
        }

        if (_backplane is not null)
        {
            await _backplane.PublishKeyInvalidationAsync($"ddos:ban:ip:{ipAddress}", cancellationToken);
        }

        PruneIfExceededCapacity();
    }

    public async ValueTask UnbanIpAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return;
        }

        _localBans.TryRemove(ipAddress, out _);
        _checkedIps.TryRemove(ipAddress, out _);

        if (_multiplexer is { IsConnected: true })
        {
            try
            {
                var db = _multiplexer.GetDatabase();
                await db.KeyDeleteAsync($"{_keyPrefix}ddos:ban:ip:{ipAddress}");
            }
            catch { }
        }

        if (_cache is not null)
        {
            await _cache.RemoveAsync($"ddos:ban:ip:{ipAddress}", cancellationToken);
        }

        if (_backplane is not null)
        {
            await _backplane.PublishKeyInvalidationAsync($"ddos:ban:ip:{ipAddress}", cancellationToken);
        }
    }

    private void PruneIfExceededCapacity()
    {
        var max = _options.Value.MaxTrackedIps;
        if (_localBans.Count > max)
        {
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var kvp in _localBans)
            {
                if (nowMs >= kvp.Value)
                {
                    _localBans.TryRemove(kvp.Key, out _);
                }
            }
        }

        if (_checkedIps.Count > max)
        {
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            foreach (var kvp in _checkedIps)
            {
                if (nowMs >= kvp.Value)
                {
                    _checkedIps.TryRemove(kvp.Key, out _);
                }
            }
        }

        if (_strikes.Count > max)
        {
            var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            var windowMs = _options.Value.StrikeWindowSeconds * 1000L;
            foreach (var kvp in _strikes)
            {
                if (nowMs - kvp.Value.WindowStartUnixMs > windowMs)
                {
                    _strikes.TryRemove(kvp.Key, out _);
                }
            }
        }
    }
}
