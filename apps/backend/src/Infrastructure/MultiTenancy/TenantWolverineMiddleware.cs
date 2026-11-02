namespace ExamPrism.Infrastructure.MultiTenancy;

using Common.MultiTenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Wolverine;

public sealed class TenantWolverineMiddleware
{
    private IMultiTenantContext<ExamPrismTenantInfo>? _priorContext;
    private bool _overridden;
    private IMultiTenantContextSetter? _setter;

    public Task BeforeAsync(
        Envelope envelope,
        IMultiTenantContextAccessor<ExamPrismTenantInfo> accessor,
        IMultiTenantContextSetter setter,
        IMultiTenantStore<ExamPrismTenantInfo> store)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(setter);
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(envelope.TenantId) ||
            string.Equals(envelope.TenantId, "*DEFAULT*", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var currentTenant = accessor.MultiTenantContext?.TenantInfo;
        if (currentTenant is not null &&
            (string.Equals(currentTenant.Id, envelope.TenantId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(currentTenant.Identifier, envelope.TenantId, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.CompletedTask;
        }

        ExamPrismTenantInfo? tenantInfo = null;
        if (Guid.TryParse(envelope.TenantId, out var tenantGuid))
        {
            tenantInfo = store.GetAsync(tenantGuid.ToString("D")).GetAwaiter().GetResult();
        }

        if (tenantInfo is null)
        {
            tenantInfo = store.GetByIdentifierAsync(envelope.TenantId.ToLowerInvariant()).GetAwaiter().GetResult();
        }

        if (tenantInfo is null || tenantInfo.Status != TenantStatus.Active)
        {
            throw new InvalidOperationException($"Cannot execute message for inactive or unknown tenant '{envelope.TenantId}'.");
        }

        _priorContext = accessor.MultiTenantContext;
        _overridden = true;
        _setter = setter;
        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo);

        return Task.CompletedTask;
    }

    public void Finally(IMultiTenantContextSetter? setter = null)
    {
        if (_overridden)
        {
            var effectiveSetter = setter ?? _setter;
            if (effectiveSetter is not null)
            {
                effectiveSetter.MultiTenantContext = _priorContext!;
            }

            _overridden = false;
            _priorContext = null;
            _setter = null;
        }
    }
}
