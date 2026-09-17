using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class ScratchArraysTests
{
    [Fact]
    public void RefillCapacity_GrowsWithAQuarterSlackAndKeepsAMinimum()
    {
        int[] values = [];
        ScratchArrays.EnsureRefillCapacity(ref values, 1, minimum: 16);
        Assert.Equal(16, values.Length);

        ScratchArrays.EnsureRefillCapacity(ref values, 1000);
        Assert.Equal(1250, values.Length);

        // Inside the band nothing happens.
        int[] same = values;
        ScratchArrays.EnsureRefillCapacity(ref values, 600);
        Assert.Same(same, values);
    }

    [Fact]
    public void RefillCapacity_ShrinksOnlyBelowAThirdAndOnlyAboveTheFloor()
    {
        int[] values = new int[ScratchArrays.ShrinkFloor];
        int[] same = values;
        ScratchArrays.EnsureRefillCapacity(ref values, 10);
        Assert.Same(same, values); // at the floor: never shrinks

        values = new int[30_000];
        same = values;
        ScratchArrays.EnsureRefillCapacity(ref values, 10_001);
        Assert.Same(same, values); // 10,001 * 3 > 30,000: kept

        ScratchArrays.EnsureRefillCapacity(ref values, 9_999);
        Assert.Equal(12_498, values.Length); // shrunk to required plus slack

        values = new int[30_000];
        ScratchArrays.EnsureRefillCapacity(ref values, 100);
        Assert.Equal(ScratchArrays.ShrinkFloor, values.Length); // never below the floor
    }

    [Fact]
    public void AppendCapacity_KeepsContentsAndNeverShrinks()
    {
        int[] values = [1, 2, 3];
        ScratchArrays.EnsureAppendCapacity(ref values, 4, minimum: 4);
        Assert.Equal(5, values.Length); // required plus a quarter of slack
        Assert.Equal([1, 2, 3], values[..3]);

        ScratchArrays.EnsureAppendCapacity(ref values, 100);
        Assert.Equal(125, values.Length);
        Assert.Equal([1, 2, 3], values[..3]);

        int[] same = values;
        ScratchArrays.EnsureAppendCapacity(ref values, 1);
        Assert.Same(same, values);
    }
}

public sealed class FrameScratchTrimTests
{
    private static int[] Peaked(int capacity)
    {
        var values = new int[capacity];
        for (int i = 0; i < capacity; i++)
            values[i] = i + 1;
        return values;
    }

    /// <summary>A buffer that a frame filled to its peak is left alone: the
    /// trim must never take capacity a steady workload is using.</summary>
    [Fact]
    public void Observe_BufferInUse_KeepsItsCapacity()
    {
        int[] buffer = Peaked(8_000);
        int[] same = buffer;
        var trim = new FrameScratchTrim(everyFrames: 4, floor: 1_024);

        for (int frame = 0; frame < 20; frame++)
            trim.Observe(ref buffer, 6_000);

        Assert.Same(same, buffer);
    }

    /// <summary>Nothing is handed back before the trim interval is up, so a
    /// quiet frame straight after a peak cannot shrink the buffer the next
    /// frame may still need.</summary>
    [Fact]
    public void Observe_BeforeTheInterval_HandsNothingBack()
    {
        int[] buffer = Peaked(40_000);
        int[] same = buffer;
        var trim = new FrameScratchTrim(everyFrames: 8, floor: 1_024);

        for (int frame = 0; frame < 7; frame++)
            trim.Observe(ref buffer, 10);

        Assert.Same(same, buffer);
        Assert.Equal(7, trim.FramesSinceTrim);
        Assert.Equal(10, trim.PeakSinceTrim);
    }

    /// <summary>After the interval the buffer comes back to the peak the
    /// window actually used. This is the pin on the arena that used to keep a
    /// portal arrival's fifteen megabytes for the rest of the session.</summary>
    [Fact]
    public void Observe_AfterTheInterval_ComesBackToTheWindowsPeak()
    {
        int[] buffer = Peaked(40_000);
        var trim = new FrameScratchTrim(everyFrames: 4, floor: 1_024);

        trim.Observe(ref buffer, 9_000);
        trim.Observe(ref buffer, 3_000);
        trim.Observe(ref buffer, 1_000);
        Assert.Equal(40_000, buffer.Length);

        trim.Observe(ref buffer, 2_000);
        Assert.Equal(9_000, buffer.Length);
        Assert.Equal(0, trim.FramesSinceTrim);
        Assert.Equal(0, trim.PeakSinceTrim);
    }

    /// <summary>The peak resets with the trim, so a window that follows a busy
    /// one is judged on its own frames and not on an old high-water mark.</summary>
    [Fact]
    public void Observe_PeakIsPerWindow()
    {
        int[] buffer = Peaked(40_000);
        var trim = new FrameScratchTrim(everyFrames: 2, floor: 1_024);

        trim.Observe(ref buffer, 12_000);
        trim.Observe(ref buffer, 1);
        Assert.Equal(12_000, buffer.Length);

        trim.Observe(ref buffer, 1);
        trim.Observe(ref buffer, 1);
        Assert.Equal(1_024, buffer.Length);
    }

    /// <summary>The floor is never crossed, so an idle stretch cannot leave a
    /// buffer that every later frame has to grow again.</summary>
    [Fact]
    public void Observe_NeverShrinksBelowTheFloor()
    {
        int[] buffer = Peaked(100_000);
        var trim = new FrameScratchTrim(everyFrames: 2, floor: 4_096);

        trim.Observe(ref buffer, 0);
        trim.Observe(ref buffer, 0);

        Assert.Equal(4_096, buffer.Length);
    }

    /// <summary>A buffer within twice the peak is left alone rather than
    /// reallocated for a few per cent.</summary>
    [Fact]
    public void Observe_WithinTwiceThePeak_DoesNotReallocate()
    {
        int[] buffer = Peaked(9_000);
        int[] same = buffer;
        var trim = new FrameScratchTrim(everyFrames: 2, floor: 1_024);

        trim.Observe(ref buffer, 5_000);
        trim.Observe(ref buffer, 5_000);

        Assert.Same(same, buffer);
    }
}
