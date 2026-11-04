namespace ExamPrism.Infrastructure.MultiTenancy;

using Common.MultiTenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Serilog.Context;
using Wolverine;

public sealed class TenantWolverineMiddleware
{
    private IMultiTenantContext<ExamPrismTenantInfo>? _priorContext;
    private bool _overridden;
    private IMultiTenantContextSetter? _setter;
    private IDisposable? _logContextToken;

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
            if (currentTenant.Status != TenantStatus.Active)
            {
                throw new InvalidOperationException($"Cannot execute message for inactive or unknown tenant '{envelope.TenantId}'.");
            }

            _logContextToken?.Dispose();
            _logContextToken = LogContext.PushProperty("TenantId", ResolveTenantLogProperty(currentTenant, envelope.TenantId));
            return Task.CompletedTask;
        }

        ExamPrismTenantInfo? tenantInfo = null;
        if (Guid.TryParse(envelope.TenantId, out var tenantGuid))
        {
            var task = store.GetAsync(tenantGuid.ToString("D"));
            tenantInfo = task.IsCompletedSuccessfully ? task.Result : task.GetAwaiter().GetResult();
        }

        if (tenantInfo is null)
        {
            var task = store.GetByIdentifierAsync(envelope.TenantId.ToLowerInvariant());
            tenantInfo = task.IsCompletedSuccessfully ? task.Result : task.GetAwaiter().GetResult();
        }

        if (tenantInfo is null || tenantInfo.Status != TenantStatus.Active)
        {
            throw new InvalidOperationException($"Cannot execute message for inactive or unknown tenant '{envelope.TenantId}'.");
        }

        _priorContext = accessor.MultiTenantContext;
        _overridden = true;
        _setter = setter;
        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo);

        _logContextToken?.Dispose();
        _logContextToken = LogContext.PushProperty("TenantId", ResolveTenantLogProperty(tenantInfo, envelope.TenantId));

        return Task.CompletedTask;
    }

    public void Finally(IMultiTenantContextSetter? setter = null)
    {
        try
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
        finally
        {
            _logContextToken?.Dispose();
            _logContextToken = null;
        }
    }

    private static object ResolveTenantLogProperty(ExamPrismTenantInfo tenantInfo, string envelopeTenantId)
    {
        if (tenantInfo.TenantGuid != Guid.Empty)
        {
            return tenantInfo.TenantGuid;
        }

        if (Guid.TryParse(envelopeTenantId, out var parsedEnvelopeGuid))
        {
            return parsedEnvelopeGuid;
        }

        if (Guid.TryParse(tenantInfo.Id, out var parsedIdGuid))
        {
            return parsedIdGuid;
        }

        return !string.IsNullOrWhiteSpace(tenantInfo.Identifier)
            ? tenantInfo.Identifier
            : (!string.IsNullOrWhiteSpace(tenantInfo.Id) ? tenantInfo.Id : envelopeTenantId);
    }
}
