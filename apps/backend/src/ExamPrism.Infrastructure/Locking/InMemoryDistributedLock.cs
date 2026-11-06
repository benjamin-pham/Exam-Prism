namespace ExamPrism.Infrastructure.Locking;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Common.Locking;

public sealed class InMemoryDistributedLock : IDistributedLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async ValueTask<IDistributedLockHandle?> TryAcquireAsync(
        string resource,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var semaphore = _locks.GetOrAdd(resource, static _ => new SemaphoreSlim(1, 1));
        var acquired = await semaphore.WaitAsync(TimeSpan.Zero, cancellationToken);
        if (!acquired)
        {
            return null;
        }

        var token = Guid.NewGuid().ToString("N");
        return new InMemoryDistributedLockHandle(resource, token, semaphore);
    }

    private sealed class InMemoryDistributedLockHandle : IDistributedLockHandle
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        public string Resource { get; }
        public string LockToken { get; }

        public InMemoryDistributedLockHandle(string resource, string lockToken, SemaphoreSlim semaphore)
        {
            Resource = resource;
            LockToken = lockToken;
            _semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
