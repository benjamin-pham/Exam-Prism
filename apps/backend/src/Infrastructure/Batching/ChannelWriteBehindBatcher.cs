namespace ExamPrism.Infrastructure.Batching;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Common.Batching;
using Common.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class ChannelWriteBehindBatcher<TItem> : BackgroundService, IWriteBehindBatcher<TItem>
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BatchingOptions _options;
    private readonly ILogger<ChannelWriteBehindBatcher<TItem>>? _logger;
    private readonly Channel<TItem> _channel;
    private int _pendingCount;

    public ChannelWriteBehindBatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<BatchingOptions> options,
        ILogger<ChannelWriteBehindBatcher<TItem>>? logger = null)
        : this(scopeFactory, options?.Value ?? new BatchingOptions(), logger)
    {
    }

    public ChannelWriteBehindBatcher(
        IServiceScopeFactory scopeFactory,
        BatchingOptions options,
        ILogger<ChannelWriteBehindBatcher<TItem>>? logger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;

        var capacity = _options.Capacity > 0 ? _options.Capacity : 10_000;
        var channelOptions = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<TItem>(channelOptions);
    }

    public int PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    public async ValueTask EnqueueAsync(TItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _channel.Writer.WriteAsync(item, cancellationToken);
        Interlocked.Increment(ref _pendingCount);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxBatchSize = Math.Max(1, _options.MaxBatchSize);
        var flushIntervalMs = Math.Max(1, _options.FlushIntervalMs);
        var batch = new List<TItem>(maxBatchSize);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!await _channel.Reader.WaitToReadAsync(stoppingToken))
                {
                    break;
                }

                while (batch.Count < maxBatchSize && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count > 0 && batch.Count < maxBatchSize)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeoutCts.CancelAfter(flushIntervalMs);

                    try
                    {
                        while (batch.Count < maxBatchSize)
                        {
                            if (await _channel.Reader.WaitToReadAsync(timeoutCts.Token))
                            {
                                while (batch.Count < maxBatchSize && _channel.Reader.TryRead(out var item))
                                {
                                    batch.Add(item);
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Flush interval elapsed; proceed to flush partial batch
                    }
                }

                if (batch.Count > 0)
                {
                    await FlushBatchInternalAsync(batch, stoppingToken);
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal graceful shutdown
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unexpected error in ChannelWriteBehindBatcher<{ItemType}>.ExecuteAsync", typeof(TItem).Name);
        }
        finally
        {
            if (batch.Count > 0)
            {
                try
                {
                    await FlushBatchInternalAsync(batch, CancellationToken.None);
                    batch.Clear();
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to flush partial batch on ChannelWriteBehindBatcher<{ItemType}> stop", typeof(TItem).Name);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();

        await base.StopAsync(cancellationToken);

        var maxBatchSize = Math.Max(1, _options.MaxBatchSize);
        var residualBatch = new List<TItem>(maxBatchSize);

        while (_channel.Reader.TryRead(out var item))
        {
            residualBatch.Add(item);
            if (residualBatch.Count >= maxBatchSize)
            {
                await FlushBatchInternalAsync(residualBatch, cancellationToken);
                residualBatch.Clear();
            }
        }

        if (residualBatch.Count > 0)
        {
            await FlushBatchInternalAsync(residualBatch, cancellationToken);
            residualBatch.Clear();
        }
    }

    private async Task FlushBatchInternalAsync(IReadOnlyList<TItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IBatchFlushHandler<TItem>>();
            await handler.FlushBatchAsync(items, cancellationToken);
            ExamPrismDiagnostics.RecordBatchFlushed(items.Count);
        }
        finally
        {
            Interlocked.Add(ref _pendingCount, -items.Count);
        }
    }
}
