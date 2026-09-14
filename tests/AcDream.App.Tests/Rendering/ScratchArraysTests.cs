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
