namespace ExamPrism.Infrastructure.Orleans;

using Common.MultiTenancy;
using Common.Orleans;
using global::Orleans;
using global::Orleans.Runtime;

public sealed class TenantGrainFactory : ITenantGrainFactory
{
    private readonly IGrainFactory _grainFactory;
    private readonly ITenantContext _tenantContext;

    public TenantGrainFactory(IGrainFactory grainFactory, ITenantContext tenantContext)
    {
        _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
    }

    public TGrainInterface GetGrain<TGrainInterface>(string entityKey)
        where TGrainInterface : ITenantScopedGrain
    {
        if (!_tenantContext.HasTenant || _tenantContext.TenantId is not { } tenantId || tenantId == Guid.Empty)
        {
            throw new InvalidOperationException("An active tenant context is required to resolve a tenant-scoped grain.");
        }

        return GetGrainForTenant<TGrainInterface>(tenantId, entityKey);
    }

    public TGrainInterface GetGrain<TGrainInterface>(Guid entityKey)
        where TGrainInterface : ITenantScopedGrain
    {
        return GetGrain<TGrainInterface>(entityKey.ToString("D"));
    }

    public TGrainInterface GetGrainForTenant<TGrainInterface>(Guid tenantId, string entityKey)
        where TGrainInterface : ITenantScopedGrain
    {
        var compoundKey = TenantGrainKey.Format(tenantId, entityKey);
        RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, tenantId.ToString("D"));
        return _grainFactory.GetGrain<TGrainInterface>(compoundKey);
    }

    public TGrainInterface GetGrainForTenant<TGrainInterface>(Guid tenantId, Guid entityKey)
        where TGrainInterface : ITenantScopedGrain
    {
        return GetGrainForTenant<TGrainInterface>(tenantId, entityKey.ToString("D"));
    }
}
