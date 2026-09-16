using AcDream.App.Plugins;

namespace AcDream.App.Tests.Plugins;

public sealed class MainThreadDispatchQueueTests
{
    [Fact]
    public void InvokeAndWaitRunsInlineOnTheOwnerThread()
    {
        var queue = new MainThreadDispatchQueue();
        int calls = 0;

        bool completed = queue.InvokeAndWait(
            () => calls++, TimeSpan.FromSeconds(1));

        Assert.True(completed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InvokeAndWaitFromAnotherThreadBlocksUntilDrained()
    {
        var queue = new MainThreadDispatchQueue();
        int calls = 0;
        using var workerReady = new ManualResetEventSlim(false);
        bool? completed = null;

        var worker = new Thread(() =>
        {
            workerReady.Set();
            completed = queue.InvokeAndWait(
                () => Interlocked.Increment(ref calls),
                TimeSpan.FromSeconds(5));
        });
        worker.IsBackground = true;
        worker.Start();

        Assert.True(workerReady.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);
        Assert.Equal(0, calls);

        queue.Drain();
        worker.Join(TimeSpan.FromSeconds(5));

        Assert.True(completed);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void InvokeAndWaitTimesOutIfNeverDrained()
    {
        var queue = new MainThreadDispatchQueue();
        using var workerReady = new ManualResetEventSlim(false);
        bool? completed = null;

        var worker = new Thread(() =>
        {
            workerReady.Set();
            completed = queue.InvokeAndWait(
                () => { }, TimeSpan.FromMilliseconds(50));
        });
        worker.IsBackground = true;
        worker.Start();

        Assert.True(workerReady.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));

        Assert.False(completed);
    }
}
