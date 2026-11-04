namespace Common.Caching;

using System.Threading;
using System.Threading.Tasks;

public interface ICacheBackplane
{
    string InstanceId { get; }

    ValueTask PublishKeyInvalidationAsync(string scopedKey, CancellationToken cancellationToken = default);

    ValueTask PublishTagInvalidationAsync(string scopedTag, CancellationToken cancellationToken = default);
}
