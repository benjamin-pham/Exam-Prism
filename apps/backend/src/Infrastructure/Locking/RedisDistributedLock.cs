namespace ExamPrism.Infrastructure.Locking;

using System;
using System.Threading;
using System.Threading.Tasks;
using Common.Locking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

public sealed class RedisDistributedLock : IDistributedLock
{
    private readonly IConnectionMultiplexer _multiplexer;
    private readonly LockingOptions _options;
    private readonly ILogger<RedisDistributedLock> _logger;
    private readonly InMemoryDistributedLock _inMemoryFallback = new();

    public RedisDistributedLock(
        IConnectionMultiplexer multiplexer,
        IOptions<LockingOptions> options,
        ILogger<RedisDistributedLock> logger)
    {
        _multiplexer = multiplexer ?? throw new ArgumentNullException(nameof(multiplexer));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<IDistributedLockHandle?> TryAcquireAsync(
        string resource,
        TimeSpan? expiry = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);

        var fullKey = $"{_options.KeyPrefix}{resource}";
        var token = Guid.NewGuid().ToString("N");
        var effectiveExpiry = expiry ?? TimeSpan.FromSeconds(_options.DefaultExpirySeconds);

        try
        {
            var db = _multiplexer.GetDatabase();
            var acquired = await db.StringSetAsync(fullKey, token, effectiveExpiry, When.NotExists);
            if (!acquired)
            {
                return null;
            }

            return new RedisDistributedLockHandle(resource, fullKey, token, db, _logger);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            _logger.LogWarning(ex, "Failed to acquire distributed lock for resource {Resource} via Redis, falling back to in-memory lock", resource);
            return await _inMemoryFallback.TryAcquireAsync(resource, expiry, cancellationToken);
        }
    }

    private sealed class RedisDistributedLockHandle : IDistributedLockHandle
    {
        private const string ReleaseLockScript = @"
if redis.call('GET', KEYS[1]) == ARGV[1] then
    return redis.call('DEL', KEYS[1])
else
    return 0
end";

        private readonly IDatabase _database;
        private readonly ILogger _logger;
        private readonly string _fullKey;
        private int _disposed;

        public string Resource { get; }
        public string LockToken { get; }

        public RedisDistributedLockHandle(
            string resource,
            string fullKey,
            string token,
            IDatabase database,
            ILogger logger)
        {
            Resource = resource;
            _fullKey = fullKey;
            LockToken = token;
            _database = database;
            _logger = logger;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await _database.ScriptEvaluateAsync(
                    ReleaseLockScript,
                    keys: [(RedisKey)_fullKey],
                    values: [(RedisValue)LockToken]);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                _logger.LogWarning(ex, "Failed to release distributed lock for resource {Resource} via Redis", Resource);
            }
        }
    }
}
