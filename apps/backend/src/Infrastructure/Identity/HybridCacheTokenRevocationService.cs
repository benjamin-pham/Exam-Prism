namespace ExamPrism.Infrastructure.Identity;

using System;
using System.Threading;
using System.Threading.Tasks;
using Common.Caching;
using Common.Security;
using Microsoft.Extensions.Options;

public sealed class HybridCacheTokenRevocationService : ITokenRevocationService
{
    private readonly ICacheService _cacheService;
    private readonly IdentityHighLoadOptions _options;
    private readonly TimeProvider _timeProvider;

    public HybridCacheTokenRevocationService(
        ICacheService cacheService,
        IOptions<IdentityHighLoadOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        _cacheService = cacheService ?? throw new ArgumentNullException(nameof(cacheService));
        _options = options?.Value ?? new IdentityHighLoadOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask RevokeTokenAsync(
        string jti,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);

        var key = GetJtiKey(jti);
        var effectiveTtl = ttl ?? _options.TokenRevocationCacheTtl;
        await _cacheService.SetAsync(key, true, expiration: effectiveTtl, cancellationToken: cancellationToken);
    }

    public async ValueTask RevokeUserTokensAsync(
        string userId,
        string? tenantId = null,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var key = GetUserRevocationKey(userId, tenantId);
        var effectiveTtl = ttl ?? _options.TokenRevocationCacheTtl;
        var revocationTime = _timeProvider.GetUtcNow().UtcDateTime;
        await _cacheService.SetAsync(key, revocationTime, expiration: effectiveTtl, cancellationToken: cancellationToken);
    }

    public ValueTask<bool> IsTokenRevokedAsync(string jti, CancellationToken cancellationToken = default) =>
        IsTokenRevokedAsync(jti, null, null, null, cancellationToken);

    public async ValueTask<bool> IsTokenRevokedAsync(
        string jti,
        string? userId,
        string? tenantId,
        DateTime? issuedAt,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(jti))
        {
            var jtiKey = GetJtiKey(jti);
            var isJtiRevoked = await _cacheService.GetOrDefaultAsync<bool>(jtiKey, cancellationToken);
            if (isJtiRevoked)
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(userId) && issuedAt.HasValue)
        {
            var userKey = GetUserRevocationKey(userId, tenantId);
            var userRevokedAt = await _cacheService.GetOrDefaultAsync<DateTime?>(userKey, cancellationToken);
            if (userRevokedAt.HasValue && issuedAt.Value <= userRevokedAt.Value)
            {
                return true;
            }

            // Cross-tenant check: if tenantId is non-empty and not "platform", also check global platform key
            if (!string.IsNullOrWhiteSpace(tenantId) &&
                !string.Equals(tenantId, "platform", StringComparison.OrdinalIgnoreCase))
            {
                var platformKey = GetUserRevocationKey(userId, "platform");
                var platformRevokedAt = await _cacheService.GetOrDefaultAsync<DateTime?>(platformKey, cancellationToken);
                if (platformRevokedAt.HasValue && issuedAt.Value <= platformRevokedAt.Value)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetJtiKey(string jti) => $"auth:revoked:jti:{jti}";

    private static string GetUserRevocationKey(string userId, string? tenantId) =>
        $"auth:revoked:user:{tenantId ?? "platform"}:{userId}";
}
