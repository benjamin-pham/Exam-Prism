namespace ExamPrism.Infrastructure.RateLimiting;

using System.Threading.RateLimiting;

public sealed class InMemoryFixedWindowRateLimiter : RateLimiter
{
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();

    private long _windowBucket = -1;
    private int _count;

    public InMemoryFixedWindowRateLimiter(int permitLimit, TimeSpan window, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _permitLimit = permitLimit;
        _window = window;
        _timeProvider = timeProvider;
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
        var windowTicks = _window.Ticks;
        var bucket = nowTicks / windowTicks;
        var retryAfter = TimeSpan.FromTicks(Math.Max(1, windowTicks - (nowTicks % windowTicks)));

        lock (_lock)
        {
            if (bucket != _windowBucket)
            {
                _windowBucket = bucket;
                _count = 0;
            }

            if (permitCount == 0)
            {
                return _count < _permitLimit
                    ? SuccessfulLease.Instance
                    : new FailedLease(retryAfter);
            }

            if (_count + permitCount <= _permitLimit)
            {
                _count += permitCount;
                return SuccessfulLease.Instance;
            }

            return new FailedLease(retryAfter);
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(AttemptAcquireCore(permitCount));
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
