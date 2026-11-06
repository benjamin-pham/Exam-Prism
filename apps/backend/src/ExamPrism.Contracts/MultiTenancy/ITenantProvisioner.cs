namespace Common.MultiTenancy;

public interface ITenantProvisioner
{
    Task ProvisionAsync(Guid tenantId, CancellationToken ct = default);
}
