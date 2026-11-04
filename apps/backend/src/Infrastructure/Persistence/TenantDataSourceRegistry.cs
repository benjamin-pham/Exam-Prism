namespace ExamPrism.Infrastructure.Persistence;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Telemetry;
using ExamPrism.Infrastructure.MultiTenancy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

public sealed class TenantDataSourceRegistry : IHostedService, IAsyncDisposable, IDisposable
{
    private readonly MultiTenancyOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantDataSourceRegistry>? _logger;
    private readonly ConcurrentDictionary<string, DataSourceEntry> _cache = new();
    private readonly object _syncRoot = new();

    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;

    public TenantDataSourceRegistry(
        IOptions<MultiTenancyOptions>? options = null,
        TimeProvider? timeProvider = null,
        ILogger<TenantDataSourceRegistry>? logger = null)
    {
        _options = options?.Value ?? new MultiTenancyOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    public int ActiveCount => _cache.Count;

    public NpgsqlDataSource GetOrCreate(string normalizedConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedConnectionString);

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        if (_cache.TryGetValue(normalizedConnectionString, out var existing))
        {
            existing.Touch(nowMs);
            return existing.DataSource;
        }

        NpgsqlDataSource? toDispose = null;

        lock (_syncRoot)
        {
            if (_cache.TryGetValue(normalizedConnectionString, out existing))
            {
                existing.Touch(nowMs);
                return existing.DataSource;
            }

            var maxCapacity = _options.DatabasePool?.MaxCachedTenantDataSources ?? 500;
            if (maxCapacity > 0 && _cache.Count >= maxCapacity)
            {
                string? oldestKey = null;
                var oldestTime = long.MaxValue;

                foreach (var kvp in _cache)
                {
                    var lastAccess = kvp.Value.LastAccessedUnixMs;
                    if (lastAccess < oldestTime)
                    {
                        oldestTime = lastAccess;
                        oldestKey = kvp.Key;
                    }
                }

                if (oldestKey != null && _cache.TryRemove(oldestKey, out var evictedEntry))
                {
                    toDispose = evictedEntry.DataSource;
                }
            }

            var dataSource = NpgsqlDataSource.Create(normalizedConnectionString);
            var entry = new DataSourceEntry(dataSource, nowMs);
            _cache[normalizedConnectionString] = entry;

            ExamPrismDiagnostics.SetActiveTenantDataSources(_cache.Count);
        }

        if (toDispose != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await toDispose.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to asynchronously dispose evicted NpgsqlDataSource.");
                }
            });
        }

        return _cache[normalizedConnectionString].DataSource;
    }

    public async ValueTask EvictIdleAsync(TimeSpan maxIdle, CancellationToken ct = default)
    {
        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var thresholdMs = nowMs - (long)maxIdle.TotalMilliseconds;

        List<NpgsqlDataSource> toDispose = [];

        lock (_syncRoot)
        {
            foreach (var kvp in _cache)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (kvp.Value.LastAccessedUnixMs < thresholdMs)
                {
                    if (_cache.TryRemove(kvp.Key, out var evicted))
                    {
                        toDispose.Add(evicted.DataSource);
                    }
                }
            }

            ExamPrismDiagnostics.SetActiveTenantDataSources(_cache.Count);
        }

        foreach (var ds in toDispose)
        {
            try
            {
                await ds.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to asynchronously dispose idle NpgsqlDataSource.");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _backgroundTask = RunIdleEvictionLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    private async Task RunIdleEvictionLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(5);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _timeProvider.Delay(interval, ct).ConfigureAwait(false);
                var idleMinutes = _options.DatabasePool?.DataSourceIdleEvictionMinutes ?? 30;
                await EvictIdleAsync(TimeSpan.FromMinutes(idleMinutes), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error occurred during tenant data source idle eviction loop.");
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_backgroundTask != null)
        {
            try
            {
                await _backgroundTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cancellation on shutdown
            }
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _cts.Dispose();
            _cts = null;
        }

        List<NpgsqlDataSource> toDispose;
        lock (_syncRoot)
        {
            toDispose = _cache.Values.Select(v => v.DataSource).ToList();
            _cache.Clear();
            ExamPrismDiagnostics.SetActiveTenantDataSources(0);
        }

        foreach (var ds in toDispose)
        {
            try
            {
                await ds.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to dispose NpgsqlDataSource during registry disposal.");
            }
        }
    }

    private sealed class DataSourceEntry(NpgsqlDataSource dataSource, long lastAccessedUnixMs)
    {
        public NpgsqlDataSource DataSource { get; } = dataSource;
        private long _lastAccessedUnixMs = lastAccessedUnixMs;

        public long LastAccessedUnixMs => Interlocked.Read(ref _lastAccessedUnixMs);

        public void Touch(long unixMs)
        {
            Interlocked.Exchange(ref _lastAccessedUnixMs, unixMs);
        }
    }
}
