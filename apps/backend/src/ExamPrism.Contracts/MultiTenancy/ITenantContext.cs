namespace Common.MultiTenancy;

public interface ITenantContext
{
    bool HasTenant { get; }
    Guid? TenantId { get; }
    string? Identifier { get; }
    string? Name { get; }
}
