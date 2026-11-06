namespace Common.Orleans;

public interface ITenantGrainFactory
{
    TGrainInterface GetGrain<TGrainInterface>(string entityKey)
        where TGrainInterface : ITenantScopedGrain;

    TGrainInterface GetGrain<TGrainInterface>(Guid entityKey)
        where TGrainInterface : ITenantScopedGrain;

    TGrainInterface GetGrainForTenant<TGrainInterface>(Guid tenantId, string entityKey)
        where TGrainInterface : ITenantScopedGrain;

    TGrainInterface GetGrainForTenant<TGrainInterface>(Guid tenantId, Guid entityKey)
        where TGrainInterface : ITenantScopedGrain;
}
