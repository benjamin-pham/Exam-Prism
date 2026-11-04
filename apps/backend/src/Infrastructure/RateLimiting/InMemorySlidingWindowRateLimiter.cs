namespace ExamPrism.Infrastructure.RateLimiting;

using System.Threading.RateLimiting;

public sealed class InMemorySlidingWindowRateLimiter : RateLimiter
{
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly int _segmentsPerWindow;
    private readonly long _segmentTicks;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();

    private readonly Segment[] _segments;
    private int _totalCount;
    private long _lastAcquiredTicks;

    public InMemorySlidingWindowRateLimiter(int permitLimit, TimeSpan window, TimeProvider timeProvider)
        : this(permitLimit, window, segmentsPerWindow: 6, timeProvider)
    {
    }

    public InMemorySlidingWindowRateLimiter(int permitLimit, TimeSpan window, int segmentsPerWindow, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentsPerWindow, 1);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var segmentTicks = window.Ticks / segmentsPerWindow;
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentTicks, 1, nameof(window));

        _permitLimit = permitLimit;
        _window = window;
        _segmentsPerWindow = segmentsPerWindow;
        _segmentTicks = segmentTicks;
        _timeProvider = timeProvider;

        _segments = new Segment[segmentsPerWindow];
        for (var i = 0; i < segmentsPerWindow; i++)
        {
            _segments[i] = new Segment(-1, 0);
        }
    }

    public override TimeSpan? IdleDuration => _lastAcquiredTicks == 0
        ? TimeSpan.Zero
        : TimeSpan.FromTicks(Math.Max(0, _timeProvider.GetUtcNow().UtcTicks - Volatile.Read(ref _lastAcquiredTicks)));

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        lock (_lock)
        {
            var nowTicks = _timeProvider.GetUtcNow().UtcTicks;
            _lastAcquiredTicks = nowTicks;

            var currentSegmentIndex = nowTicks / _segmentTicks;

            // Evict expired segments
            for (var i = 0; i < _segments.Length; i++)
            {
                if (_segments[i].Index != -1 && _segments[i].Index <= currentSegmentIndex - _segmentsPerWindow)
                {
                    _totalCount -= _segments[i].Count;
                    _segments[i] = new Segment(-1, 0);
                }
            }

            var slot = (int)((currentSegmentIndex % _segmentsPerWindow + _segmentsPerWindow) % _segmentsPerWindow);
            if (_segments[slot].Index != currentSegmentIndex)
            {
                if (_segments[slot].Index != -1)
                {
                    _totalCount -= _segments[slot].Count;
                }
                _segments[slot] = new Segment(currentSegmentIndex, 0);
            }

            var retryAfter = CalculateRetryAfter(nowTicks, currentSegmentIndex);

            if (permitCount == 0)
            {
                return _totalCount < _permitLimit
                    ? SuccessfulLease.Instance
                    : new FailedLease(retryAfter);
            }

            if (_totalCount + permitCount <= _permitLimit)
            {
                _totalCount += permitCount;
                _segments[slot] = new Segment(currentSegmentIndex, _segments[slot].Count + permitCount);
                return SuccessfulLease.Instance;
            }

            return new FailedLease(retryAfter);
        }
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(AttemptAcquireCore(permitCount));
    }

    private TimeSpan CalculateRetryAfter(long nowTicks, long currentSegmentIndex)
    {
        var oldestIndex = long.MaxValue;
        for (var i = 0; i < _segments.Length; i++)
        {
            if (_segments[i].Index != -1 && _segments[i].Count > 0 && _segments[i].Index < oldestIndex)
            {
                oldestIndex = _segments[i].Index;
            }
        }

        if (oldestIndex == long.MaxValue)
        {
            return _window;
        }

        var expireTicks = (oldestIndex + _segmentsPerWindow) * _segmentTicks;
        var retryAfterTicks = Math.Max(1, expireTicks - nowTicks);
        return TimeSpan.FromTicks(retryAfterTicks);
    }

    private readonly record struct Segment(long Index, int Count);

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
