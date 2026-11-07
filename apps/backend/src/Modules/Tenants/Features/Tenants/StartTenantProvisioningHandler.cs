namespace ExamPrism.Features.Tenants;

using Common.Handlers;
using Common.MultiTenancy;
using Common.Results;

public record StartTenantProvisioningCommand(Guid TenantId);

public sealed class StartTenantProvisioningHandler(ITenantProvisioner provisioner) : IHandler<StartTenantProvisioningCommand>
{
    public async Task<Result> Handle(StartTenantProvisioningCommand command, CancellationToken ct = default)
    {
        await provisioner.ProvisionAsync(command.TenantId, ct);
        return Result.Success();
    }
}
