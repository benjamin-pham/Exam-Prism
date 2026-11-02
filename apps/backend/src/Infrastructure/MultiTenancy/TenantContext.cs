namespace ExamPrism.Infrastructure.MultiTenancy;

using Common.MultiTenancy;
using Finbuckle.MultiTenant.Abstractions;

public sealed class TenantContext(IMultiTenantContextAccessor<ExamPrismTenantInfo> accessor) : ITenantContext
{
    private ExamPrismTenantInfo? ActiveTenant =>
        accessor.MultiTenantContext?.TenantInfo is { Status: TenantStatus.Active } info
            ? info
            : null;

    public bool HasTenant => ActiveTenant is not null;

    public Guid? TenantId => ActiveTenant?.TenantGuid;

    public string? Identifier => ActiveTenant?.Identifier;

    public string? Name => ActiveTenant?.Name;
}
