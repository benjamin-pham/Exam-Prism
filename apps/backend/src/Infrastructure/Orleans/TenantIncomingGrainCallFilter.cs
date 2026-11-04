namespace ExamPrism.Infrastructure.Orleans;

using Common.MultiTenancy;
using Common.Orleans;
using ExamPrism.Infrastructure.MultiTenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using global::Orleans;
using global::Orleans.Runtime;

public sealed class TenantIncomingGrainCallFilter : IIncomingGrainCallFilter
{
    private readonly IServiceScopeFactory _scopeFactory;

    public TenantIncomingGrainCallFilter(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    public async Task Invoke(IIncomingGrainCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Grain is not ITenantScopedGrain)
        {
            await context.Invoke();
            return;
        }

        string? rawKey = null;
        try
        {
            if (context.Grain is IAddressable addressable)
            {
                rawKey = addressable.GetPrimaryKeyString();
            }
        }
        catch
        {
            // Grain runtime or context may not be initialized on mock grains
        }

        if (string.IsNullOrEmpty(rawKey))
        {
            rawKey = context.TargetId.Key.ToString();
        }

        rawKey ??= string.Empty;

        if (!TenantGrainKey.TryParse(rawKey, out var grainKey))
        {
            throw new InvalidOperationException(
                $"Invalid tenant grain key format '{rawKey}'. Expected '{{tenantId}}:{{entityKey}}'.");
        }

        var requestTenantId = RequestContext.Get(OrleansTenantConstants.TenantIdRequestContextKey) as string;
        if (!string.IsNullOrWhiteSpace(requestTenantId))
        {
            if (!Guid.TryParse(requestTenantId, out var parsedRequestTenantId) || parsedRequestTenantId != grainKey.TenantId)
            {
                throw new InvalidOperationException(
                    $"Cross-tenant grain call rejected: RequestContext tenant '{requestTenantId}' does not match grain key tenant '{grainKey.TenantId:D}'.");
            }
        }
        else
        {
            RequestContext.Set(OrleansTenantConstants.TenantIdRequestContextKey, grainKey.TenantId.ToString("D"));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var accessor = sp.GetRequiredService<IMultiTenantContextAccessor<ExamPrismTenantInfo>>();
        var setter = sp.GetRequiredService<IMultiTenantContextSetter>();

        var tenantIdString = grainKey.TenantId.ToString("D");
        var currentTenant = accessor.MultiTenantContext?.TenantInfo;
        ExamPrismTenantInfo? tenantInfo = null;

        if (currentTenant is not null &&
            currentTenant.Status == TenantStatus.Active &&
            (string.Equals(currentTenant.Id, tenantIdString, StringComparison.OrdinalIgnoreCase) ||
             currentTenant.TenantGuid == grainKey.TenantId))
        {
            tenantInfo = currentTenant;
        }
        else
        {
            var store = sp.GetRequiredService<IMultiTenantStore<ExamPrismTenantInfo>>();
            tenantInfo = await store.GetAsync(tenantIdString);
        }

        if (tenantInfo is null || tenantInfo.Status != TenantStatus.Active)
        {
            throw new InvalidOperationException(
                $"Cannot execute grain call for inactive or unknown tenant '{grainKey.TenantId:D}'.");
        }

        var priorContext = accessor.MultiTenantContext;
        setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo);
        try
        {
            await context.Invoke();
        }
        finally
        {
            setter.MultiTenantContext = priorContext!;
        }
    }
}
