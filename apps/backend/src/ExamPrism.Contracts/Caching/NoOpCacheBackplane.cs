namespace Common.Caching;

using System;
using System.Threading;
using System.Threading.Tasks;

public sealed class NoOpCacheBackplane : ICacheBackplane
{
    public string InstanceId { get; } = "noop";

    public event Action<string>? KeyInvalidated
    {
        add { }
        remove { }
    }

    public event Action<string>? TagInvalidated
    {
        add { }
        remove { }
    }

    public ValueTask PublishKeyInvalidationAsync(string scopedKey, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask PublishTagInvalidationAsync(string scopedTag, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
