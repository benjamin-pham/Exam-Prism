namespace ExamPrism.Infrastructure.Caching;

using Common.Caching;
using Common.MultiTenancy;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

public sealed class HybridCacheService : ICacheService
{
    private readonly HybridCache _hybridCache;
    private readonly ICacheBackplane _backplane;
    private readonly CacheOptions _options;
    private readonly IMultiTenantContextAccessor<ExamPrismTenantInfo>? _tenantAccessor;

    public HybridCacheService(
        HybridCache hybridCache,
        IOptions<CacheOptions> options,
        ICacheBackplane? backplane = null,
        IMultiTenantContextAccessor<ExamPrismTenantInfo>? tenantAccessor = null)
    {
        _hybridCache = hybridCache ?? throw new ArgumentNullException(nameof(hybridCache));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _backplane = backplane ?? new NoOpCacheBackplane();
        _tenantAccessor = tenantAccessor;
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

        var (scopePrefix, tenantInfo) = GetCurrentScope();
        var scopedKey = scopePrefix + key;
        var scopedTags = ResolveTags(tenantInfo, scopePrefix, tags);
        var options = CreateEntryOptions(expiration, localExpiration);

        return await _hybridCache.GetOrCreateAsync(
            scopedKey,
            factory,
            options,
            scopedTags,
            cancellationToken);
    }

    public async ValueTask<T?> GetOrDefaultAsync<T>(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var (scopePrefix, _) = GetCurrentScope();
        var scopedKey = scopePrefix + key;

        var options = new HybridCacheEntryOptions
        {
            Flags = HybridCacheEntryFlags.DisableUnderlyingData
        };

        return await _hybridCache.GetOrCreateAsync<object?, T?>(
            scopedKey,
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

        var (scopePrefix, tenantInfo) = GetCurrentScope();
        var scopedKey = scopePrefix + key;
        var scopedTags = ResolveTags(tenantInfo, scopePrefix, tags);
        var options = CreateEntryOptions(expiration, localExpiration);

        await _hybridCache.SetAsync(
            scopedKey,
            value,
            options,
            scopedTags,
            cancellationToken);

        await _backplane.PublishKeyInvalidationAsync(scopedKey, cancellationToken);
    }

    public async ValueTask RemoveAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var (scopePrefix, _) = GetCurrentScope();
        var scopedKey = scopePrefix + key;
        await _hybridCache.RemoveAsync(scopedKey, cancellationToken);
        await _backplane.PublishKeyInvalidationAsync(scopedKey, cancellationToken);
    }

    public async ValueTask RemoveByTagAsync(
        string tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var (scopePrefix, _) = GetCurrentScope();
        var scopedTag = scopePrefix + tag;
        await _hybridCache.RemoveByTagAsync(scopedTag, cancellationToken);
        await _backplane.PublishTagInvalidationAsync(scopedTag, cancellationToken);
    }

    private (string ScopePrefix, ExamPrismTenantInfo? TenantInfo) GetCurrentScope()
    {
        var tenantInfo = _tenantAccessor?.MultiTenantContext?.TenantInfo;
        var scopePrefix = tenantInfo is { Status: TenantStatus.Active }
            ? $"t:{tenantInfo.Id}:"
            : "platform:";
        return (scopePrefix, tenantInfo);
    }

    private static IReadOnlyCollection<string>? ResolveTags(
        ExamPrismTenantInfo? tenantInfo,
        string scopePrefix,
        IEnumerable<string>? tags)
    {
        var hasActiveTenant = tenantInfo is { Status: TenantStatus.Active };
        var umbrellaTag = hasActiveTenant ? $"t:{tenantInfo!.Id}" : null;

        if (tags is null)
        {
            return umbrellaTag is not null ? [umbrellaTag] : null;
        }

        var scopedTags = tags.Select(t => scopePrefix + t).ToList();
        if (umbrellaTag is not null && !scopedTags.Contains(umbrellaTag, StringComparer.Ordinal))
        {
            scopedTags.Add(umbrellaTag);
        }

        return scopedTags;
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
