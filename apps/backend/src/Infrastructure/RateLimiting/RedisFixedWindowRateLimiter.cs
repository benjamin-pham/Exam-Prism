namespace ExamPrism.Infrastructure.RateLimiting;

using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

public sealed class RedisFixedWindowRateLimiter : RateLimiter
{
    private const string LuaScript = """
local current = redis.call('INCRBY', KEYS[1], ARGV[1])
if current == tonumber(ARGV[1]) or redis.call('PTTL', KEYS[1]) < 0 then
    redis.call('PEXPIRE', KEYS[1], ARGV[2])
end
return current
""";

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly string _redisKeyPrefix;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private long _lastAcquiredTicks;

    public RedisFixedWindowRateLimiter(
        IConnectionMultiplexer multiplexer,
        string redisKeyPrefix,
        int permitLimit,
        TimeSpan window,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentException.ThrowIfNullOrWhiteSpace(redisKeyPrefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _multiplexer = multiplexer;
        _redisKeyPrefix = redisKeyPrefix;
        _permitLimit = permitLimit;
        _window = window;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public override TimeSpan? IdleDuration => _lastAcquiredTicks == 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(Math.Max(0, _timeProvider.GetUtcNow().UtcTicks - Volatile.Read(ref _lastAcquiredTicks)));

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        Interlocked.Exchange(ref _lastAcquiredTicks, nowTicks);

        var windowTicks = _window.Ticks;
        var bucket = nowTicks / windowTicks;
        var retryAfter = TimeSpan.FromTicks(Math.Max(1, windowTicks - (nowTicks % windowTicks)));
        var key = $"{_redisKeyPrefix}:{bucket}";
        var windowMs = Math.Max(1L, (long)Math.Ceiling(_window.TotalMilliseconds));

        try
        {
            var db = _multiplexer.GetDatabase();
            var result = db.ScriptEvaluate(
                LuaScript,
                [key],
                [permitCount, windowMs]);

            var current = (long)result;
            return current <= _permitLimit
                ? SuccessfulLease.Instance
                : new FailedLease(retryAfter);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Redis rate limiter failed for key {RateLimitKey}; failing open.", key);
            return SuccessfulLease.Instance;
        }
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        Interlocked.Exchange(ref _lastAcquiredTicks, nowTicks);

        var windowTicks = _window.Ticks;
        var bucket = nowTicks / windowTicks;
        var retryAfter = TimeSpan.FromTicks(Math.Max(1, windowTicks - (nowTicks % windowTicks)));
        var key = $"{_redisKeyPrefix}:{bucket}";
        var windowMs = Math.Max(1L, (long)Math.Ceiling(_window.TotalMilliseconds));

        try
        {
            var db = _multiplexer.GetDatabase();
            var result = await db.ScriptEvaluateAsync(
                LuaScript,
                [key],
                [permitCount, windowMs]);

            var current = (long)result;
            return current <= _permitLimit
                ? SuccessfulLease.Instance
                : new FailedLease(retryAfter);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Redis rate limiter failed for key {RateLimitKey}; failing open.", key);
            return SuccessfulLease.Instance;
        }
    }

    private sealed class SuccessfulLease : RateLimitLease
    {
        public static readonly SuccessfulLease Instance = new();

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private sealed class FailedLease(TimeSpan retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}

