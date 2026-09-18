using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanDeferredReleaseQueueTests
{
    [Fact]
    public void NothingIsReleasedUntilItIsHandedOver()
    {
        var released = new ConcurrentQueue<int>();
        using var queue = new VulkanDeferredReleaseQueue<int>(released.Enqueue, "test-release");

        // The point of the class: holding an item costs nothing and releases
        // nothing. A frame that has not retired must not have its staging
        // memory freed, and the only thing standing between the two is that
        // the caller has not called Enqueue yet.
        Thread.Sleep(20);
        Assert.Empty(released);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0L, queue.EnqueuedCount);
        Assert.Equal(0L, queue.ReleasedCount);

        queue.Enqueue(7);
        queue.Drain();

        Assert.Equal([7], released.ToArray());
    }

    [Fact]
    public void AFlightsTemporariesAreReleasedOnlyOnceItsFlightRetires()
    {
        // The wiring the upload queue uses: the release is registered with the
        // flight ledger and only reaches the queue when the timeline has passed
        // that frame's serial, so a buffer a submitted command buffer still
        // reads is never freed underneath it.
        var timeline = new FakeTimeline();
        var flights = new VulkanFrameFlightController(timeline, framesInFlight: 2);
        var released = new ConcurrentQueue<string>();
        using var queue = new VulkanDeferredReleaseQueue<string>(released.Enqueue, "test-release");

        long serial = flights.BeginFrame();
        flights.Retire(() => queue.Enqueue($"staged-in-{serial}"));
        flights.EndFrame();

        // Frame 2 opens while frame 1 is still executing on the device.
        flights.BeginFrame();
        queue.Drain();
        Assert.Empty(released);

        timeline.Value = 1;
        flights.EndFrame();
        flights.BeginFrame();
        queue.Drain();

        Assert.Equal(["staged-in-1"], released.ToArray());
    }

    [Fact]
    public void EveryItemIsReleasedExactlyOnce_InTheOrderItWasHandedOver()
    {
        var released = new ConcurrentQueue<int>();
        using var queue = new VulkanDeferredReleaseQueue<int>(released.Enqueue, "test-release");

        for (int item = 0; item < 500; item++)
            queue.Enqueue(item);
        queue.Drain();

        int[] seen = [.. released];
        Assert.Equal(500, seen.Length);
        Assert.Equal(Enumerable.Range(0, 500), seen);
        Assert.Equal(500L, queue.EnqueuedCount);
        Assert.Equal(500L, queue.ReleasedCount);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void TheReleaseRunsOnTheWorkerAndNotOnTheThreadThatHandedItOver()
    {
        // The whole reason the class exists: the frame thread must not pay the
        // driver's free. If a change ever releases inline, this fails.
        int callerThread = Environment.CurrentManagedThreadId;
        var releasingThreads = new ConcurrentQueue<int>();
        using var queue = new VulkanDeferredReleaseQueue<int>(
            _ => releasingThreads.Enqueue(Environment.CurrentManagedThreadId),
            "test-release");

        for (int item = 0; item < 50; item++)
            queue.Enqueue(item);
        queue.Drain();

        Assert.Equal(50, releasingThreads.Count);
        Assert.DoesNotContain(callerThread, releasingThreads);
        Assert.Single(releasingThreads.Distinct());
    }

    [Fact]
    public void DisposeReleasesEverythingStillQueuedBeforeItReturns()
    {
        var released = new ConcurrentQueue<int>();
        var admit = new ManualResetEventSlim(false);
        var queue = new VulkanDeferredReleaseQueue<int>(
            item =>
            {
                // Hold the worker on the first item so the rest are still
                // queued when Dispose is called.
                if (item == 0)
                    admit.Wait(TimeSpan.FromSeconds(5));
                released.Enqueue(item);
            },
            "test-release");

        for (int item = 0; item < 100; item++)
            queue.Enqueue(item);
        Assert.True(queue.PendingCount > 0);

        admit.Set();
        queue.Dispose();

        Assert.Equal(100, released.Count);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(queue.EnqueuedCount, queue.ReleasedCount);
    }

    [Fact]
    public void AHandOverAfterDisposeIsReleasedOnTheCallingThread()
    {
        // Teardown drains the flight ledger more than once; a retirement that
        // runs after the queue is gone still has to free its memory, and by
        // then the device is idle.
        var releasingThreads = new ConcurrentQueue<int>();
        var queue = new VulkanDeferredReleaseQueue<int>(
            _ => releasingThreads.Enqueue(Environment.CurrentManagedThreadId),
            "test-release");
        queue.Dispose();

        queue.Enqueue(1);

        Assert.Equal([Environment.CurrentManagedThreadId], releasingThreads.ToArray());
        Assert.Equal(1L, queue.EnqueuedCount);
        Assert.Equal(1L, queue.ReleasedCount);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public void TheCountsBalanceWhileTheWorkerIsRunning()
    {
        // enqueued == released + pending, sampled while the worker is mid-flight:
        // an item taken off the queue but not yet freed is still this queue's
        // responsibility, so it has to be counted as pending.
        var gate = new ManualResetEventSlim(false);
        using var queue = new VulkanDeferredReleaseQueue<int>(_ => gate.Wait(TimeSpan.FromSeconds(5)), "test-release");

        for (int item = 0; item < 5; item++)
            queue.Enqueue(item);

        for (int sample = 0; sample < 200; sample++)
        {
            long enqueued = queue.EnqueuedCount;
            long releasedThenPending = queue.ReleasedCount + queue.PendingCount;
            Assert.True(
                enqueued == releasedThenPending,
                $"sample {sample}: enqueued {enqueued} != released+pending {releasedThenPending}");
        }

        gate.Set();
        queue.Drain();
        Assert.Equal(5L, queue.ReleasedCount);
        Assert.Equal(0, queue.PendingCount);
    }

    private sealed class FakeTimeline : IVulkanTimelineApi
    {
        public ulong Value { get; set; }

        public ulong CurrentValue => Value;

        public void Wait(ulong value) => Value = Math.Max(Value, value);
    }
}
