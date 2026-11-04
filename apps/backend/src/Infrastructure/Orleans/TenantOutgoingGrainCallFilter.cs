namespace ExamPrism.Infrastructure.Orleans;

using Common.MultiTenancy;
using Common.Orleans;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;
using global::Orleans;
using global::Orleans.Runtime;

public sealed class TenantOutgoingGrainCallFilter : IOutgoingGrainCallFilter
{
    private readonly IMultiTenantContextAccessor<ExamPrismTenantInfo> _accessor;

    public TenantOutgoingGrainCallFilter(IMultiTenantContextAccessor<ExamPrismTenantInfo> accessor)
    {
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    public Task Invoke(IOutgoingGrainCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var existingTenantId = RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey) as string;
        if (string.IsNullOrWhiteSpace(existingTenantId))
        {
            var tenantInfo = _accessor.MultiTenantContext?.TenantInfo;
            if (tenantInfo is { Status: TenantStatus.Active })
            {
                var tenantId = tenantInfo.TenantGuid != Guid.Empty
                    ? tenantInfo.TenantGuid
                    : (Guid.TryParse(tenantInfo.Id, out var parsedGuid) ? parsedGuid : Guid.Empty);

                if (tenantId != Guid.Empty)
                {
                    RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, tenantId.ToString("D"));
                }
            }
        }

        return context.Invoke();
    }
}
