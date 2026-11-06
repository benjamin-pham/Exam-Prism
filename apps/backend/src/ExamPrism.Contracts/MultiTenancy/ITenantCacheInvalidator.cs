namespace Common.MultiTenancy;

public interface ITenantCacheInvalidator
{
    ValueTask InvalidateTenantAsync(Guid tenantId, string slug, CancellationToken ct = default);
}

