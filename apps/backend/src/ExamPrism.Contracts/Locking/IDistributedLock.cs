namespace Common.Locking;

using System;
using System.Threading;
using System.Threading.Tasks;

public interface IDistributedLockHandle : IAsyncDisposable
{
    string Resource { get; }
    string LockToken { get; }
}

public interface IDistributedLock
{
    ValueTask<IDistributedLockHandle?> TryAcquireAsync(
        string resource,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default);
}
