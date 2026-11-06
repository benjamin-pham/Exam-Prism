namespace Common.Caching;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface ICacheBackplane
{
    string InstanceId { get; }

    event Action<string>? KeyInvalidated;

    event Action<string>? TagInvalidated;

    ValueTask PublishKeyInvalidationAsync(string scopedKey, CancellationToken cancellationToken = default);

    ValueTask PublishTagInvalidationAsync(string scopedTag, CancellationToken cancellationToken = default);
}
