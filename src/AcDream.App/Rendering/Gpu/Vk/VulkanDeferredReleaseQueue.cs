namespace AcDream.App.Rendering.Gpu.Vk;

/// <summary>
/// Hands releases that must not run on the frame thread to one background
/// thread of its own.
/// <para>What it guarantees is ownership, and the counts are the proof:
/// an item handed over is released exactly once, on the worker, and
/// <see cref="EnqueuedCount"/> always equals <see cref="ReleasedCount"/> plus
/// <see cref="PendingCount"/> — an item taken off the queue but not yet
/// released still counts as pending, so a drain that returns has really
/// finished the work rather than merely emptied the queue.</para>
/// <para>The caller decides when an item may be handed over; this queue never
/// delays a release beyond that point, it only moves it off the caller's
/// thread. After <see cref="Dispose"/> a late hand-over is released on the
/// calling thread instead, the same way the flight ledger runs a retirement
/// inline once it has been torn down: the release still has to happen and
/// nothing is in flight by then.</para>
/// </summary>
internal sealed class VulkanDeferredReleaseQueue<T> : IDisposable
{
    private readonly Action<T> _release;
    private readonly Queue<T> _queued = new();
    private readonly object _sync = new();
    private readonly Thread _worker;

    private int _releasing;
    private long _enqueued;
    private long _released;
    private bool _stopping;
    private bool _disposed;

    internal VulkanDeferredReleaseQueue(Action<T> release, string threadName)
    {
        _release = release ?? throw new ArgumentNullException(nameof(release));
        ArgumentException.ThrowIfNullOrWhiteSpace(threadName);
        _worker = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName,
        };
        _worker.Start();
    }

    /// <summary>Items handed over and not yet released, the one being released included.</summary>
    internal int PendingCount
    {
        get { lock (_sync) return _queued.Count + _releasing; }
    }

    internal long EnqueuedCount
    {
        get { lock (_sync) return _enqueued; }
    }

    internal long ReleasedCount
    {
        get { lock (_sync) return _released; }
    }

    internal void Enqueue(T item)
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                _enqueued++;
                _queued.Enqueue(item);
                Monitor.Pulse(_sync);
                return;
            }

            _enqueued++;
            _releasing++;
        }

        ReleaseOne(item);
    }

    /// <summary>Blocks until everything handed over so far has been released.</summary>
    internal void Drain()
    {
        lock (_sync)
        {
            while (_queued.Count + _releasing > 0)
                Monitor.Wait(_sync);
        }
    }

    private void Run()
    {
        while (true)
        {
            T item;
            lock (_sync)
            {
                while (_queued.Count == 0 && !_stopping)
                    Monitor.Wait(_sync);
                if (_queued.Count == 0)
                    return;
                item = _queued.Dequeue();
                _releasing++;
            }

            ReleaseOne(item);
        }
    }

    private void ReleaseOne(T item)
    {
        try
        {
            _release(item);
        }
        finally
        {
            lock (_sync)
            {
                _releasing--;
                _released++;
                Monitor.PulseAll(_sync);
            }
        }
    }

    /// <summary>Stops the worker after it has released everything handed over.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stopping = true;
            Monitor.PulseAll(_sync);
        }

        _worker.Join();
    }
}
