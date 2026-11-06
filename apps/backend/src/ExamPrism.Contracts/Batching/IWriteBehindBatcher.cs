namespace Common.Batching;

using System.Threading;
using System.Threading.Tasks;

public interface IWriteBehindBatcher<in TItem>
{
    ValueTask EnqueueAsync(TItem item, CancellationToken cancellationToken = default);

    int PendingCount { get; }
}
