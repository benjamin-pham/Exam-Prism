namespace ExamPrism.Infrastructure.MultiTenancy;

using Common.MultiTenancy;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Wolverine;
using Wolverine.Configuration;

public sealed class MultiTenantMessageBus : IMessageBus
{
    private readonly IMessageBus _inner;
    private readonly IMultiTenantStore<ExamPrismTenantInfo> _store;
    private readonly IMultiTenantContextSetter _setter;
    private readonly IMultiTenantContextAccessor<ExamPrismTenantInfo> _accessor;

    public MultiTenantMessageBus(
        IMessageBus inner,
        IMultiTenantStore<ExamPrismTenantInfo> store,
        IMultiTenantContextSetter setter,
        IMultiTenantContextAccessor<ExamPrismTenantInfo> accessor)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _setter = setter ?? throw new ArgumentNullException(nameof(setter));
        _accessor = accessor ?? throw new ArgumentNullException(nameof(accessor));
    }

    public string? TenantId
    {
        get => _inner.TenantId;
        set => _inner.TenantId = value;
    }

    public async Task InvokeForTenantAsync(
        string tenantId,
        object message,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(message);

        var tenantInfo = await ResolveTenantAsync(tenantId);
        var prior = _accessor.MultiTenantContext;
        _setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo);
        try
        {
            await _inner.InvokeForTenantAsync(tenantId, message, cancellation, timeout);
        }
        finally
        {
            _setter.MultiTenantContext = prior!;
        }
    }

    public async Task<T> InvokeForTenantAsync<T>(
        string tenantId,
        object message,
        CancellationToken cancellation = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(message);

        var tenantInfo = await ResolveTenantAsync(tenantId);
        var prior = _accessor.MultiTenantContext;
        _setter.MultiTenantContext = new MultiTenantContext<ExamPrismTenantInfo>(tenantInfo);
        try
        {
            return await _inner.InvokeForTenantAsync<T>(tenantId, message, cancellation, timeout);
        }
        finally
        {
            _setter.MultiTenantContext = prior!;
        }
    }

    public Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        => _inner.InvokeAsync(message, cancellation, timeout);

    public Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        => _inner.InvokeAsync<T>(message, cancellation, timeout);

    public ValueTask SendAsync<T>(T message, DeliveryOptions? options = null)
        => _inner.SendAsync(message, options);

    public ValueTask PublishAsync<T>(T message, DeliveryOptions? options = null)
        => _inner.PublishAsync(message, options);

    public ValueTask BroadcastToTopicAsync(string topicName, object message, DeliveryOptions? options = null)
        => _inner.BroadcastToTopicAsync(topicName, message, options);

    public IDestinationEndpoint EndpointFor(string endpointName)
        => _inner.EndpointFor(endpointName);

    public IDestinationEndpoint EndpointFor(Uri uri)
        => _inner.EndpointFor(uri);

    public IReadOnlyList<Envelope> PreviewSubscriptions(object message)
        => _inner.PreviewSubscriptions(message);

    private async Task<ExamPrismTenantInfo> ResolveTenantAsync(string tenantId)
    {
        ExamPrismTenantInfo? tenantInfo = null;
        if (Guid.TryParse(tenantId, out var tenantGuid))
        {
            tenantInfo = await _store.GetAsync(tenantGuid.ToString("D"));
        }

        tenantInfo ??= await _store.GetByIdentifierAsync(tenantId.ToLowerInvariant());

        if (tenantInfo is null || tenantInfo.Status != TenantStatus.Active)
        {
            throw new InvalidOperationException($"Cannot execute message for inactive or unknown tenant '{tenantId}'.");
        }

        return tenantInfo;
    }
}
