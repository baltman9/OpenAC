using System;
using System.Collections.Generic;
using System.Threading;

namespace AcDream.Core.Chat;

public readonly record struct SpewBoxEntry(string Text, double ExpiresAtSeconds);

public sealed class SpewBoxState
{
    public const int MaxConcurrentItems = 4;

    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Queue<string> _pending = new();
    private readonly List<SpewBoxEntry> _visible = new();
    private string? _lastPending;
    private long _revision;

    public long Revision => Interlocked.Read(ref _revision);

    public int Count
    {
        get { lock (_gate) return _visible.Count; }
    }

    /// <summary>Lines enqueued since the last <see cref="Tick"/>.</summary>
    internal int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>
    /// Queues a line for the next <see cref="Tick"/>. The queue never holds
    /// more than <see cref="MaxConcurrentItems"/> lines, so a host that never
    /// ticks (a windowless client with no console) does not accumulate every
    /// status line of its lifetime. Nothing a tick would show is lost:
    /// a line equal to the one queued just before it is a no-op for the tick
    /// (it replaces that line with the same expiry), and once repeats are
    /// folded, a tick keeps only the newest <see cref="MaxConcurrentItems"/>
    /// lines, which are exactly the ones retained here.
    /// </summary>
    public void Enqueue(string text)
    {
        lock (_gate)
        {
            if (_pending.Count > 0 && _lastPending == text)
                return;

            _pending.Enqueue(text);
            _lastPending = text;
            while (_pending.Count > MaxConcurrentItems)
                _pending.Dequeue();
        }
    }

    public void Tick(double nowSeconds)
    {
        bool changed;
        lock (_gate)
        {
            changed = _pending.Count > 0;
            while (_pending.Count > 0)
            {
                string text = _pending.Dequeue();

                if (_visible.Count > 0 && _visible[0].Text == text)
                    _visible.RemoveAt(0);

                _visible.Insert(0, new SpewBoxEntry(text, nowSeconds + DefaultLifetime.TotalSeconds));

                while (_visible.Count > MaxConcurrentItems)
                    _visible.RemoveAt(_visible.Count - 1);
            }

            int removed = _visible.RemoveAll(e => e.ExpiresAtSeconds <= nowSeconds);
            changed |= removed > 0;
        }

        if (changed)
            Interlocked.Increment(ref _revision);
    }

    public SpewBoxEntry[] Snapshot()
    {
        lock (_gate)
            return _visible.ToArray();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
            _visible.Clear();
        }
        Interlocked.Increment(ref _revision);
    }
}
