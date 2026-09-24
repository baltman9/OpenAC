using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class SpewBoxStateTests
{
    [Fact]
    public void Enqueue_DoesNotBecomeVisibleUntilTick()
    {
        var state = new SpewBoxState();
        state.Enqueue("You can't jump while in the air");

        Assert.Equal(0, state.Count);
        Assert.Empty(state.Snapshot());
    }

    [Fact]
    public void Tick_DrainsPendingIntoVisible()
    {
        var state = new SpewBoxState();
        state.Enqueue("You can't jump while in the air");

        state.Tick(nowSeconds: 0d);

        Assert.Equal(1, state.Count);
        SpewBoxEntry entry = Assert.Single(state.Snapshot());
        Assert.Equal("You can't jump while in the air", entry.Text);
    }

    [Fact]
    public void Tick_InsertsNewestAtIndexZero()
    {
        var state = new SpewBoxState();
        state.Enqueue("first");
        state.Tick(0d);
        state.Enqueue("second");
        state.Tick(0d);

        Assert.Equal(2, state.Count);
        SpewBoxEntry[] snapshot = state.Snapshot();
        Assert.Equal("second", snapshot[0].Text);
        Assert.Equal("first", snapshot[1].Text);
    }

    [Fact]
    public void Tick_IdenticalRepeat_RefreshesInPlace_DoesNotStack()
    {
        var state = new SpewBoxState();
        state.Enqueue("You are too encumbered to carry that!");
        state.Tick(0d);
        state.Enqueue("You are too encumbered to carry that!");
        state.Tick(1d);

        Assert.Equal(1, state.Count);
        SpewBoxEntry entry = Assert.Single(state.Snapshot());
        Assert.Equal("You are too encumbered to carry that!", entry.Text);
        Assert.Equal(1d + SpewBoxState.DefaultLifetime.TotalSeconds, entry.ExpiresAtSeconds);
    }

    [Fact]
    public void Tick_DifferentText_DoesNotDedupe()
    {
        var state = new SpewBoxState();
        state.Enqueue("first message");
        state.Tick(0d);
        state.Enqueue("second message");
        state.Tick(0d);

        Assert.Equal(2, state.Count);
        SpewBoxEntry[] snapshot = state.Snapshot();
        Assert.Equal("second message", snapshot[0].Text);
        Assert.Equal("first message", snapshot[1].Text);
    }

    [Fact]
    public void Tick_Overflow_DropsOldest_RespectingMaxConcurrentItems()
    {
        var state = new SpewBoxState();
        Assert.Equal(4, SpewBoxState.MaxConcurrentItems);

        for (int i = 0; i < SpewBoxState.MaxConcurrentItems + 1; i++)
        {
            state.Enqueue($"line {i}");
            state.Tick(0d);
        }

        Assert.Equal(SpewBoxState.MaxConcurrentItems, state.Count);
        SpewBoxEntry[] snapshot = state.Snapshot();
        // Newest at index 0; "line 0" (the oldest) dropped by overflow.
        Assert.Equal("line 4", snapshot[0].Text);
        Assert.Equal("line 1", snapshot[3].Text);
        Assert.DoesNotContain(snapshot, e => e.Text == "line 0");
    }

    [Fact]
    public void Tick_PrunesExpiredEntries()
    {
        var state = new SpewBoxState();
        state.Enqueue("fading message");
        state.Tick(nowSeconds: 0d);
        Assert.Equal(1, state.Count);

        double justPastExpiry = SpewBoxState.DefaultLifetime.TotalSeconds + 0.001;
        state.Tick(justPastExpiry);

        Assert.Equal(0, state.Count);
        Assert.Empty(state.Snapshot());
    }

    [Fact]
    public void Tick_NoPendingNoExpired_RevisionUnchanged()
    {
        var state = new SpewBoxState();
        state.Enqueue("stays visible a while");
        state.Tick(0d);
        long revisionAfterFirstTick = state.Revision;

        // Well within the lifetime window, nothing pending — a second Tick
        // should be a pure no-op.
        state.Tick(0.5d);

        Assert.Equal(revisionAfterFirstTick, state.Revision);
    }

    [Fact]
    public void Reset_ClearsPendingAndVisible()
    {
        var state = new SpewBoxState();
        state.Enqueue("pending, never ticked");
        state.Enqueue("about to be visible");
        state.Tick(0d);
        Assert.True(state.Count > 0);

        state.Reset();

        Assert.Equal(0, state.Count);
        Assert.Empty(state.Snapshot());

        // A Reset while text was still pending (never ticked) must also
        // discard the pending queue — ticking afterward shows nothing.
        state.Tick(1d);
        Assert.Equal(0, state.Count);
    }

    [Fact]
    public void Enqueue_WithoutTick_PendingStaysBounded()
    {
        // A windowless host with no console never ticks the box; a long
        // session's status lines must not pile up behind it.
        var state = new SpewBoxState();

        for (int i = 0; i < 10_000; i++)
            state.Enqueue($"line {i % 7}");

        Assert.True(
            state.PendingCount <= SpewBoxState.MaxConcurrentItems,
            $"pending grew to {state.PendingCount}");

        state.Tick(0d);
        SpewBoxEntry[] snapshot = state.Snapshot();
        Assert.Equal(
            new[] { "line 3", "line 2", "line 1", "line 0" },
            snapshot.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void Enqueue_Bounded_TickShowsExactlyWhatAnUnboundedQueueWould()
    {
        // The bound must be invisible to a host that does tick: for any
        // burst between two ticks, starting from any visible set, the
        // visible lines and their expiries equal an unbounded queue's.
        var random = new Random(4891);
        string[] alphabet = ["a", "b", "c", "d", "e", "f"];
        for (int trial = 0; trial < 2_000; trial++)
        {
            var state = new SpewBoxState();
            var model = new UnboundedModel();
            double now = 0d;
            int bursts = random.Next(1, 5);
            for (int burst = 0; burst < bursts; burst++)
            {
                int lines = random.Next(0, 12);
                for (int i = 0; i < lines; i++)
                {
                    string text = alphabet[random.Next(alphabet.Length)];
                    state.Enqueue(text);
                    model.Enqueue(text);
                }
                now += random.Next(0, 3);
                state.Tick(now);
                model.Tick(now);
                Assert.Equal(model.Visible, state.Snapshot());
            }
        }
    }

    /// <summary>The box's tick over a queue that is never trimmed.</summary>
    private sealed class UnboundedModel
    {
        private readonly List<string> _pending = new();
        public List<SpewBoxEntry> Visible { get; } = new();

        public void Enqueue(string text) => _pending.Add(text);

        public void Tick(double now)
        {
            foreach (string text in _pending)
            {
                if (Visible.Count > 0 && Visible[0].Text == text)
                    Visible.RemoveAt(0);
                Visible.Insert(0, new SpewBoxEntry(
                    text, now + SpewBoxState.DefaultLifetime.TotalSeconds));
                while (Visible.Count > SpewBoxState.MaxConcurrentItems)
                    Visible.RemoveAt(Visible.Count - 1);
            }
            _pending.Clear();
            Visible.RemoveAll(e => e.ExpiresAtSeconds <= now);
        }
    }

    [Fact]
    public void Revision_AdvancesOnTickThatChangesVisibleSet()
    {
        var state = new SpewBoxState();
        long initial = state.Revision;

        state.Enqueue("a line");
        state.Tick(0d);

        Assert.True(state.Revision > initial);
    }
}
