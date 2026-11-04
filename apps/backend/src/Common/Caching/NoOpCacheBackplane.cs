namespace Common.Caching;

using System.Threading;
using System.Threading.Tasks;

public sealed class NoOpCacheBackplane : ICacheBackplane
{
    public string InstanceId { get; } = "noop";

    public ValueTask PublishKeyInvalidationAsync(string scopedKey, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask PublishTagInvalidationAsync(string scopedTag, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
