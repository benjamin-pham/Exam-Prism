namespace ExamPrism.Infrastructure.RateLimiting;

using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

public sealed class RedisSlidingWindowRateLimiter : RateLimiter
{
    private const string LuaScript = """
local key = KEYS[1]
local nowMs = tonumber(ARGV[1])
local windowMs = tonumber(ARGV[2])
local limit = tonumber(ARGV[3])
local member = ARGV[4]
local clearBefore = nowMs - windowMs

redis.call('ZREMRANGEBYSCORE', key, '-inf', clearBefore)
local current = redis.call('ZCARD', key)
if current < limit then
    redis.call('ZADD', key, nowMs, member)
    redis.call('PEXPIRE', key, windowMs)
    return { 1, limit - current - 1, 0 }
else
    local oldest = redis.call('ZRANGE', key, 0, 0, 'WITHSCORES')
    local retryMs = windowMs
    if oldest[2] then
        retryMs = math.max(1, (tonumber(oldest[2]) + windowMs) - nowMs)
    end
    return { 0, 0, retryMs }
end
""";

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly string _redisKey;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private long _lastAcquiredTicks;

    public RedisSlidingWindowRateLimiter(
        IConnectionMultiplexer multiplexer,
        string redisKey,
        int permitLimit,
        TimeSpan window,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(multiplexer);
        ArgumentException.ThrowIfNullOrWhiteSpace(redisKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _multiplexer = multiplexer;
        _redisKey = redisKey;
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

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var windowMs = Math.Max(1L, (long)Math.Ceiling(_window.TotalMilliseconds));
        var member = $"{nowMs}:{Guid.NewGuid():N}";

        try
        {
            var db = _multiplexer.GetDatabase();
            var result = db.ScriptEvaluate(
                LuaScript,
                [_redisKey],
                [nowMs, windowMs, _permitLimit, member]);

            return ParseResult(result);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Redis sliding window rate limiter failed for key {RateLimitKey}; failing open.", _redisKey);
            return SuccessfulLease.Instance;
        }
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        Interlocked.Exchange(ref _lastAcquiredTicks, nowTicks);

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var windowMs = Math.Max(1L, (long)Math.Ceiling(_window.TotalMilliseconds));
        var member = $"{nowMs}:{Guid.NewGuid():N}";

        try
        {
            var db = _multiplexer.GetDatabase();
            var result = await db.ScriptEvaluateAsync(
                LuaScript,
                [_redisKey],
                [nowMs, windowMs, _permitLimit, member]);

            return ParseResult(result);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Redis sliding window rate limiter failed for key {RateLimitKey}; failing open.", _redisKey);
            return SuccessfulLease.Instance;
        }
    }

    private static RateLimitLease ParseResult(RedisResult? result)
    {
        if (result is not null)
        {
            var items = (RedisResult[]?)result;
            if (items is { Length: >= 3 })
            {
                var allowed = (long)items[0];
                var retryMs = (long)items[2];

                if (allowed == 1)
                {
                    return SuccessfulLease.Instance;
                }

                return new FailedLease(TimeSpan.FromMilliseconds(Math.Max(1, retryMs)));
            }
        }

        return SuccessfulLease.Instance;
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
