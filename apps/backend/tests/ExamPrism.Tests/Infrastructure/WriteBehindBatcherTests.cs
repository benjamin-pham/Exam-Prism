namespace ExamPrism.Tests.Infrastructure;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Common.Batching;
using Common.Telemetry;
using ExamPrism.Infrastructure.Batching;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

public class WriteBehindBatcherTests : IDisposable
{
    public WriteBehindBatcherTests()
    {
        ExamPrismDiagnostics.ResetForTesting();
    }

    public void Dispose()
    {
        ExamPrismDiagnostics.ResetForTesting();
    }

    [Fact]
    public async Task ChannelWriteBehindBatcher_FlushesImmediatelyWhenMaxBatchSizeReached_AndFlushesPartialBatchOnTimeout()
    {
        var services = new ServiceCollection();
        var handler = new TestBatchFlushHandler<int>();
        services.AddSingleton(handler);
        services.AddScoped<IBatchFlushHandler<int>>(sp => sp.GetRequiredService<TestBatchFlushHandler<int>>());
        var sp = services.BuildServiceProvider();

        var options = new BatchingOptions
        {
            MaxBatchSize = 3,
            FlushIntervalMs = 50,
            Capacity = 100
        };

        var batcher = new ChannelWriteBehindBatcher<int>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options));

        using var cts = new CancellationTokenSource();
        await batcher.StartAsync(cts.Token);

        try
        {
            for (var i = 1; i <= 5; i++)
            {
                await batcher.EnqueueAsync(i, cts.Token);
            }

            // Wait until 5 items are flushed or timeout after 2 seconds
            var timeoutAt = DateTime.UtcNow.AddSeconds(2);
            while (handler.TotalItemCount < 5 && DateTime.UtcNow < timeoutAt)
            {
                await Task.Delay(10);
            }

            handler.FlushedBatches.Should().HaveCount(2, "3 items flush immediately, 2 items flush on interval timeout");
            handler.FlushedBatches[0].Should().Equal(1, 2, 3);
            handler.FlushedBatches[1].Should().Equal(4, 5);
            ExamPrismDiagnostics.BatcherFlushedItemsCount.Should().Be(5);
        }
        finally
        {
            await batcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ChannelWriteBehindBatcher_DrainsRemainingBufferedItemsOnGracefulStopAsync()
    {
        var services = new ServiceCollection();
        var handler = new TestBatchFlushHandler<int>();
        services.AddSingleton(handler);
        services.AddScoped<IBatchFlushHandler<int>>(sp => sp.GetRequiredService<TestBatchFlushHandler<int>>());
        var sp = services.BuildServiceProvider();

        var options = new BatchingOptions
        {
            MaxBatchSize = 10,
            FlushIntervalMs = 10_000, // Large timeout so timer won't fire during test
            Capacity = 100
        };

        var batcher = new ChannelWriteBehindBatcher<int>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options));

        await batcher.StartAsync(CancellationToken.None);

        // Enqueue items right before StopAsync
        for (var i = 1; i <= 5; i++)
        {
            await batcher.EnqueueAsync(i);
        }

        // Graceful stop should drain all items before completing
        await batcher.StopAsync(CancellationToken.None);

        handler.TotalItemCount.Should().Be(5, "all remaining queued items must be drained during StopAsync");
        var flattened = handler.FlushedBatches.SelectMany(b => b).ToList();
        flattened.Should().Equal(1, 2, 3, 4, 5);
        batcher.PendingCount.Should().Be(0);
        ExamPrismDiagnostics.BatcherFlushedItemsCount.Should().Be(5);
    }

    [Fact]
    public async Task BatchingSetup_RegistersWriteBehindBatcherAndHostedServiceInDI()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppBatching(opts =>
        {
            opts.MaxBatchSize = 5;
            opts.FlushIntervalMs = 30;
        });
        services.AddWriteBehindBatcher<string, StringBatchFlushHandler>();

        var sp = services.BuildServiceProvider();
        var batcher = sp.GetService<IWriteBehindBatcher<string>>();
        batcher.Should().NotBeNull();

        var hostedServices = sp.GetServices<IHostedService>().ToList();
        hostedServices.Should().Contain(h => h == batcher);

        var concreteBatcher = sp.GetService<ChannelWriteBehindBatcher<string>>();
        concreteBatcher.Should().BeSameAs(batcher);
    }

    private sealed class TestBatchFlushHandler<T> : IBatchFlushHandler<T>
    {
        private readonly List<IReadOnlyList<T>> _flushedBatches = [];
        private readonly object _lock = new();

        public IReadOnlyList<IReadOnlyList<T>> FlushedBatches
        {
            get
            {
                lock (_lock)
                {
                    return _flushedBatches.Select(b => (IReadOnlyList<T>)b.ToList()).ToList();
                }
            }
        }

        public int TotalItemCount
        {
            get
            {
                lock (_lock)
                {
                    return _flushedBatches.Sum(b => b.Count);
                }
            }
        }

        public Task FlushBatchAsync(IReadOnlyList<T> items, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _flushedBatches.Add(items.ToList());
            }
            return Task.CompletedTask;
        }
    }

    public sealed class StringBatchFlushHandler : IBatchFlushHandler<string>
    {
        public Task FlushBatchAsync(IReadOnlyList<string> items, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
