namespace Common.Batching;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public interface IBatchFlushHandler<TItem>
{
    Task FlushBatchAsync(IReadOnlyList<TItem> items, CancellationToken cancellationToken = default);
}
