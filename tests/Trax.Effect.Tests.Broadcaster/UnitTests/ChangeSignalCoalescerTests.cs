using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trax.Effect.Services.ChangeSignal;

namespace Trax.Effect.Tests.Broadcaster.UnitTests;

[TestFixture]
public class ChangeSignalCoalescerTests
{
    // A small window keeps the tests fast. Correctness (one flush per burst, distinct domains)
    // holds for any positive window, so the exact value does not race: the assertions poll on the
    // real flush, never on a fixed sleep.
    private static ChangeSignalOptions FastOptions() =>
        new() { CoalesceWindow = TimeSpan.FromMilliseconds(30) };

    private sealed class CapturingSink : IChangeSignalSink
    {
        private readonly object _lock = new();
        public List<ChangeDomain[]> Flushes { get; } = new();

        public Func<Task>? OnFlush { get; set; }

        public async Task FlushAsync(
            IReadOnlyCollection<ChangeDomain> domains,
            CancellationToken ct
        )
        {
            if (OnFlush is not null)
                await OnFlush();
            lock (_lock)
                Flushes.Add(domains.ToArray());
        }

        public int Count
        {
            get
            {
                lock (_lock)
                    return Flushes.Count;
            }
        }

        /// <summary>Thread-safe union of every domain delivered across all flushes.</summary>
        public HashSet<ChangeDomain> AllDomains()
        {
            lock (_lock)
                return Flushes.SelectMany(f => f).ToHashSet();
        }
    }

    private static (
        TraxChangeSignal Signal,
        ChangeSignalCoalescer Coalescer,
        ServiceProvider Provider
    ) Build(ChangeSignalOptions options, params IChangeSignalSink[] sinks)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        foreach (var sink in sinks)
            services.AddSingleton(sink);
        var provider = services.BuildServiceProvider();

        var signal = new TraxChangeSignal(options);
        var coalescer = new ChangeSignalCoalescer(
            signal,
            provider,
            options,
            TimeProvider.System,
            provider.GetService<ILogger<ChangeSignalCoalescer>>()
        );
        return (signal, coalescer, provider);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            // allowed-delay: poll interval, not a sleep-for-sync. Bounded by the caller's timeout
            // and exits the instant the condition holds.
            await Task.Delay(15);
        }
        return condition();
    }

    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(10);

    [Test]
    public async Task SameDomainBurst_CoalescesToSingleFlush()
    {
        var sink = new CapturingSink();
        var (signal, coalescer, provider) = Build(FastOptions(), sink);
        await using var _ = provider;

        // Buffer the burst before the coalescer starts, so the first drain sees the whole burst.
        signal.Notify(ChangeDomain.WorkQueue);
        signal.Notify(ChangeDomain.WorkQueue);
        signal.Notify(ChangeDomain.WorkQueue);

        await coalescer.StartAsync(CancellationToken.None);
        try
        {
            var ok = await WaitUntilAsync(() => sink.Count == 1, FlushTimeout);
            ok.Should().BeTrue("a burst of one domain should produce exactly one flush");
            sink.Flushes[0].Should().ContainSingle().Which.Should().Be(ChangeDomain.WorkQueue);
        }
        finally
        {
            await coalescer.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task DistinctDomainsInWindow_FlushContainsEachOnce()
    {
        var sink = new CapturingSink();
        var (signal, coalescer, provider) = Build(FastOptions(), sink);
        await using var _ = provider;

        signal.Notify(ChangeDomain.WorkQueue);
        signal.Notify(ChangeDomain.DeadLetter);
        signal.Notify(ChangeDomain.WorkQueue);
        signal.Notify(ChangeDomain.Manifest);

        await coalescer.StartAsync(CancellationToken.None);
        try
        {
            var ok = await WaitUntilAsync(() => sink.Count == 1, FlushTimeout);
            ok.Should().BeTrue("distinct domains in one window collapse into a single flush");
            sink.Flushes[0]
                .Should()
                .BeEquivalentTo(
                    new[] { ChangeDomain.WorkQueue, ChangeDomain.DeadLetter, ChangeDomain.Manifest }
                );
        }
        finally
        {
            await coalescer.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task SeparateBursts_ProduceSeparateFlushes()
    {
        var sink = new CapturingSink();
        var (signal, coalescer, provider) = Build(FastOptions(), sink);
        await using var _ = provider;

        await coalescer.StartAsync(CancellationToken.None);
        try
        {
            signal.Notify(ChangeDomain.WorkQueue);
            (await WaitUntilAsync(() => sink.Count == 1, FlushTimeout)).Should().BeTrue();

            // A signal that arrives after the first flush opens a new window.
            signal.Notify(ChangeDomain.DeadLetter);
            (await WaitUntilAsync(() => sink.Count == 2, FlushTimeout)).Should().BeTrue();

            sink.Flushes[0].Should().ContainSingle().Which.Should().Be(ChangeDomain.WorkQueue);
            sink.Flushes[1].Should().ContainSingle().Which.Should().Be(ChangeDomain.DeadLetter);
        }
        finally
        {
            await coalescer.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task NoSignals_NeverFlushes_ThenWakesOnFirstSignal()
    {
        var sink = new CapturingSink();
        var (signal, coalescer, provider) = Build(FastOptions(), sink);
        await using var _ = provider;

        await coalescer.StartAsync(CancellationToken.None);
        try
        {
            // negative-wait: the coalescer blocks on an empty channel, so without a signal it
            // cannot flush. The bounded wait proves the absence; the outcome is structural, not
            // timing-dependent, so a longer delay would only slow the test, never change the result.
            await Task.Delay(150);
            sink.Count.Should().Be(0, "an idle coalescer must not flush");

            signal.Notify(ChangeDomain.SchedulerConfig);
            var ok = await WaitUntilAsync(() => sink.Count == 1, FlushTimeout);
            ok.Should().BeTrue("the coalescer wakes as soon as a real signal arrives");
            sink.Flushes[0]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(ChangeDomain.SchedulerConfig);
        }
        finally
        {
            await coalescer.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task Flush_ReachesEverySink()
    {
        var sinkA = new CapturingSink();
        var sinkB = new CapturingSink();
        var (signal, coalescer, provider) = Build(FastOptions(), sinkA, sinkB);
        await using var _ = provider;

        signal.Notify(ChangeDomain.ManifestGroup);

        await coalescer.StartAsync(CancellationToken.None);
        try
        {
            var ok = await WaitUntilAsync(() => sinkA.Count == 1 && sinkB.Count == 1, FlushTimeout);
            ok.Should().BeTrue("every registered sink receives the flush");
            sinkA.Flushes[0].Should().ContainSingle().Which.Should().Be(ChangeDomain.ManifestGroup);
            sinkB.Flushes[0].Should().ContainSingle().Which.Should().Be(ChangeDomain.ManifestGroup);
        }
        finally
        {
            await coalescer.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task SinkThrows_LoopKeepsRunning()
    {
        var throwing = new CapturingSink
        {
            OnFlush = () => throw new InvalidOperationException("boom"),
        };
        var healthy = new CapturingSink();
        var (signal, coalescer, provider) = Build(FastOptions(), throwing, healthy);
        await using var _ = provider;

        await coalescer.StartAsync(CancellationToken.None);
        try
        {
            signal.Notify(ChangeDomain.WorkQueue);
            // The healthy sink still receives the flush even though the other sink threw...
            (await WaitUntilAsync(() => healthy.Count == 1, FlushTimeout))
                .Should()
                .BeTrue();

            // ...and a later burst still flushes, proving the loop survived the exception.
            signal.Notify(ChangeDomain.DeadLetter);
            (await WaitUntilAsync(() => healthy.Count == 2, FlushTimeout)).Should().BeTrue();
        }
        finally
        {
            await coalescer.StopAsync(CancellationToken.None);
        }
    }

    [Test]
    public async Task UnderHeavyConcurrentLoad_CoalescesToFarFewerFlushesAndDeliversEveryDomain()
    {
        // The whole point of the coalescer is to bound refetches under a write storm. Hammer it
        // with 100k signals from several threads and prove two things: every distinct domain is
        // delivered, and the flush count collapses by orders of magnitude (not one flush per signal).
        var options = new ChangeSignalOptions
        {
            // Large buffer so this correctness test never exercises the drop path (covered separately).
            ChannelCapacity = 500_000,
            CoalesceWindow = TimeSpan.FromMilliseconds(20),
        };
        var sink = new CapturingSink();
        var (signal, coalescer, provider) = Build(options, sink);
        await using var _ = provider;

        await coalescer.StartAsync(CancellationToken.None);
        try
        {
            const int threads = 5;
            const int perThread = 20_000;
            const int total = threads * perThread;
            var domains = Enum.GetValues<ChangeDomain>();

            Exception? writerFailure = null;
            await Task.WhenAll(
                Enumerable
                    .Range(0, threads)
                    .Select(t =>
                        Task.Run(() =>
                        {
                            try
                            {
                                for (var i = 0; i < perThread; i++)
                                    signal.Notify(domains[(t + i) % domains.Length]);
                            }
                            catch (Exception ex)
                            {
                                writerFailure = ex;
                            }
                        })
                    )
            );

            writerFailure.Should().BeNull("Notify must never throw, even under concurrent load");

            var delivered = await WaitUntilAsync(
                () => sink.AllDomains().SetEquals(domains),
                FlushTimeout
            );
            delivered.Should().BeTrue("every domain touched under load should be delivered");

            // Let the pipeline quiesce, then assert the coalescing bound.
            await WaitUntilQuiescentAsync(sink);
            sink.Count.Should()
                .BeLessThan(
                    total / 50,
                    "coalescing must collapse the storm into orders-of-magnitude fewer flushes"
                );
        }
        finally
        {
            await coalescer.StopAsync(CancellationToken.None);
        }
    }

    // Waits until the flush count stops growing (two consecutive stable reads), i.e. the coalescer
    // has drained the backlog. Bounded so a stuck pipeline fails the test rather than hanging.
    private static async Task WaitUntilQuiescentAsync(CapturingSink sink)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        var last = -1;
        while (DateTime.UtcNow < deadline)
        {
            var now = sink.Count;
            if (now == last)
                return;
            last = now;
            // allowed-delay: poll interval, not a sleep-for-sync. Bounded by the 10s deadline and
            // returns as soon as two consecutive reads match (the backlog is drained).
            await Task.Delay(50);
        }
    }

    [Test]
    public void Notify_WhenChannelFull_DropsWithoutThrowingAndCounts()
    {
        var signal = new TraxChangeSignal(new ChangeSignalOptions { ChannelCapacity = 2 });

        // Nothing is draining, so writes past the capacity are dropped rather than queued.
        var act = () =>
        {
            for (var i = 0; i < 5; i++)
                signal.Notify(ChangeDomain.WorkQueue);
        };

        act.Should().NotThrow("Notify must never throw on a hot write path");
        signal.TotalDropped.Should().Be(3, "two fit in the buffer and three are dropped");
        signal.Dispose();
    }
}
