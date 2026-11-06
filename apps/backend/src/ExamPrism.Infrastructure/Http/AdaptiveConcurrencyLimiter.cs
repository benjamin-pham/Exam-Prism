namespace ExamPrism.Infrastructure.Http;

using System;
using System.Threading;
using Common.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

public class AdaptiveConcurrencyLimiter
{
    private readonly LoadSheddingOptions _options;
    private readonly object _syncLock = new();

    private int _inFlight;
    private int _sampleCount;
    private double _currentLimit;
    private double _minLatencyMs;
    private double _ewmaLatencyMs;

    public AdaptiveConcurrencyLimiter(IOptions<LoadSheddingOptions> options)
        : this(options.Value)
    {
    }

    public AdaptiveConcurrencyLimiter(LoadSheddingOptions options)
    {
        _options = options ?? new LoadSheddingOptions();
        _currentLimit = _options.InitialConcurrencyLimit;
        _minLatencyMs = _options.BaselineLatencyFloorMs;
        _ewmaLatencyMs = _options.BaselineLatencyFloorMs;
    }

    public AdaptiveConcurrencyLimiter()
        : this(new LoadSheddingOptions())
    {
    }

    public double CurrentLimit
    {
        get
        {
            lock (_syncLock)
            {
                return _currentLimit;
            }
        }
    }

    public double MinLatencyMs
    {
        get
        {
            lock (_syncLock)
            {
                return _minLatencyMs;
            }
        }
    }

    public double EwmaLatencyMs
    {
        get
        {
            lock (_syncLock)
            {
                return _ewmaLatencyMs;
            }
        }
    }

    public int InFlight => Volatile.Read(ref _inFlight);

    public bool TryAcquire(RequestPriority priority, out IDisposable? lease)
    {
        if (!_options.Enabled)
        {
            Interlocked.Increment(ref _inFlight);
            lease = new ConcurrencyLease(this);
            return true;
        }

        double limit = CurrentLimit;
        double maxAllowed = priority switch
        {
            RequestPriority.Low => limit * _options.LowPriorityShedFraction,
            RequestPriority.Normal => limit * _options.NormalPriorityShedFraction,
            RequestPriority.High => limit * _options.HighPriorityShedFraction,
            RequestPriority.Critical => limit * _options.CriticalReserveMultiplier,
            _ => limit * _options.NormalPriorityShedFraction
        };

        while (true)
        {
            int current = Volatile.Read(ref _inFlight);
            if (current >= maxAllowed)
            {
                lease = null;
                return false;
            }

            if (Interlocked.CompareExchange(ref _inFlight, current + 1, current) == current)
            {
                lease = new ConcurrencyLease(this);
                return true;
            }
        }
    }

    public void RecordSample(double durationMs, bool isServerError = false)
    {
        lock (_syncLock)
        {
            _sampleCount++;
            if (_options.MinLatencyResetIntervalSamples > 0 && _sampleCount >= _options.MinLatencyResetIntervalSamples)
            {
                _sampleCount = 0;
                _minLatencyMs = Math.Max(_options.BaselineLatencyFloorMs, _ewmaLatencyMs);
            }

            _minLatencyMs = Math.Min(_minLatencyMs, durationMs);

            double alpha = _options.SmoothingFactor > 0 && _options.SmoothingFactor < 1
                ? _options.SmoothingFactor
                : 0.2;

            _ewmaLatencyMs = (1.0 - alpha) * _ewmaLatencyMs + alpha * durationMs;

            double effectiveMin = Math.Max(_options.BaselineLatencyFloorMs, _minLatencyMs);
            double effectiveEwma = Math.Max(_options.BaselineLatencyFloorMs, _ewmaLatencyMs);
            double gradient = Math.Clamp(effectiveMin / effectiveEwma, 0.5, 1.0);

            bool threadPoolOverloaded = ThreadPool.PendingWorkItemCount > _options.MaxPendingThreadPoolWorkItems;

            if (gradient < _options.LatencyGradientThreshold || threadPoolOverloaded || isServerError)
            {
                double factor = (isServerError && gradient > 0.7) ? 0.7 : gradient;
                _currentLimit = Math.Max(_options.MinConcurrencyLimit, _currentLimit * factor);
            }
            else if (_inFlight >= _currentLimit * 0.5)
            {
                _currentLimit = Math.Min(_options.MaxConcurrencyLimit, _currentLimit + 1.0);
            }
        }
    }

    private void Release()
    {
        Interlocked.Decrement(ref _inFlight);
    }

    private sealed class ConcurrencyLease : IDisposable
    {
        private readonly AdaptiveConcurrencyLimiter _limiter;
        private int _disposed;

        public ConcurrencyLease(AdaptiveConcurrencyLimiter limiter)
        {
            _limiter = limiter;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _limiter.Release();
            }
        }
    }
}
