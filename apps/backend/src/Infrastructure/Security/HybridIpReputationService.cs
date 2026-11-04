namespace ExamPrism.Infrastructure.Security;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Common.Caching;
using Common.Security;
using Common.Telemetry;
using Microsoft.Extensions.Options;

public sealed class HybridIpReputationService : IIpReputationService
{
    private sealed record StrikeState(long WindowStartUnixMs, int Score);

    private readonly IOptions<DdosProtectionOptions> _options;
    private readonly ICacheService? _cache;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<string, long> _localBans = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StrikeState> _strikes = new(StringComparer.OrdinalIgnoreCase);

    public HybridIpReputationService(
        IOptions<DdosProtectionOptions> options,
        ICacheService? cache = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cache = cache;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<TimeSpan?> GetBanRemainingAsync(string ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (_localBans.TryGetValue(ipAddress, out var expiresAt))
        {
            if (nowMs < expiresAt)
            {
                return TimeSpan.FromMilliseconds(expiresAt - nowMs);
            }

            _localBans.TryRemove(ipAddress, out _);
        }

        if (_cache is not null)
        {
            var cacheKey = $"ddos:ban:ip:{ipAddress}";
            var cachedExpiry = await _cache.GetOrDefaultAsync<long?>(cacheKey, cancellationToken);
            if (cachedExpiry.HasValue)
            {
                if (nowMs < cachedExpiry.Value)
                {
                    _localBans[ipAddress] = cachedExpiry.Value;
                    return TimeSpan.FromMilliseconds(cachedExpiry.Value - nowMs);
                }

                await _cache.RemoveAsync(cacheKey, cancellationToken);
            }
        }

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

        if (newState.Score >= opt.BanThresholdScore)
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
        ExamPrismDiagnostics.RecordIpBanIssued();

        if (_cache is not null)
        {
            await _cache.SetAsync($"ddos:ban:ip:{ipAddress}", expiresAt, duration, duration, tags: null, cancellationToken);
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
        if (_cache is not null)
        {
            await _cache.RemoveAsync($"ddos:ban:ip:{ipAddress}", cancellationToken);
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
