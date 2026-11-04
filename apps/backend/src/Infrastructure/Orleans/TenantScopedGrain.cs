namespace ExamPrism.Infrastructure.Orleans;

using Common.Orleans;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using global::Orleans;
using global::Orleans.Runtime;
using Microsoft.Extensions.DependencyInjection;

public abstract class TenantScopedGrain : Grain, ITenantScopedGrain
{
    protected TenantScopedGrain()
    {
    }

    protected TenantScopedGrain(IGrainContext? grainContext)
        : base(grainContext!)
    {
    }

    public TenantGrainKey TenantKey { get; private set; }
    protected Guid TenantId => TenantKey.TenantId;
    protected string EntityKey => TenantKey.EntityKey;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var rawKey = this.GetPrimaryKeyString();
        if (!TenantGrainKey.TryParse(rawKey, out var parsedKey))
        {
            throw new InvalidOperationException(
                $"Grain '{GetType().Name}' cannot activate with invalid tenant key '{rawKey}'. Expected '{{tenantId}}:{{entityKey}}'.");
        }

        TenantKey = parsedKey;
        return base.OnActivateAsync(cancellationToken);
    }

    protected async Task ExecuteInTenantScopeAsync(
        Func<IServiceProvider, Task> action,
        CancellationToken cancellationToken = default)
    {
        await ExecuteInTenantScopeAsync<object?>(async sp =>
        {
            await action(sp);
            return null;
        }, cancellationToken);
    }

    protected async Task<TResult> ExecuteInTenantScopeAsync<TResult>(
        Func<IServiceProvider, Task<TResult>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var scope = ServiceProvider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var accessor = sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        var setter = sp.GetRequiredService<IMultiTenantContextSetter>();
        var store = sp.GetRequiredService<IMultiTenantStore<ExamPrismTenantInfo>>();

        var tenantIdString = TenantId.ToString("D");
        var currentTenant = accessor.MultiTenantContext?.TenantInfo;
        var priorContext = accessor.MultiTenantContext;
        var overridden = false;

        if (currentTenant is null ||
            !string.Equals(currentTenant.Id, tenantIdString, StringComparison.OrdinalIgnoreCase) ||
            currentTenant.Status != Common.MultiTenancy.TenantStatus.Active)
        {
            var tenantInfo = await store.GetAsync(tenantIdString);
            if (tenantInfo is null || tenantInfo.Status != Common.MultiTenancy.TenantStatus.Active)
            {
                throw new InvalidOperationException(
                    $"Cannot execute scoped grain operation for inactive or unknown tenant '{tenantIdString}'.");
            }

            setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo);
            overridden = true;
        }

        RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, tenantIdString);

        try
        {
            return await action(sp);
        }
        finally
        {
            if (overridden)
            {
                setter.MultiTenantContext = priorContext!;
            }
        }
    }
}
