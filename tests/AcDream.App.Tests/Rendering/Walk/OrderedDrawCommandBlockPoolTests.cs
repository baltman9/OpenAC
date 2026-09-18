using AcDream.App.Rendering.Walk;

namespace AcDream.App.Tests.Rendering.Walk;

/// <summary>
/// The pool that keeps command-block array sets alive between the producer
/// that releases them and the one that takes them next.
/// </summary>
public sealed class OrderedDrawCommandBlockPoolTests
{
    [Fact]
    public void TakingWithNothingParkedAllocates()
    {
        var pool = new OrderedDrawCommandBlockPool();

        OrderedDrawCommandBlock block = pool.Take(7);

        Assert.Equal(7, block.Capacity);
        Assert.Equal(0, block.Count);
        Assert.Equal(1, pool.AllocationCount);
        Assert.Equal(0, pool.ReuseCount);
        Assert.Equal(0, pool.ParkedCapacity);
    }

    [Fact]
    public void AParkedSetIsTakenAgainInsteadOfAllocating()
    {
        var pool = new OrderedDrawCommandBlockPool();
        OrderedDrawCommandBlock first = pool.Take(7);
        pool.Park(first);
        Assert.Equal(7, pool.ParkedCapacity);

        OrderedDrawCommandBlock second = pool.Take(7);

        Assert.Same(first, second);
        Assert.Equal(1, pool.AllocationCount);
        Assert.Equal(1, pool.ReuseCount);
        Assert.Equal(0, pool.ParkedCapacity);
    }

    [Fact]
    public void ATakeIsServedBySmallestSetThatFits()
    {
        var pool = new OrderedDrawCommandBlockPool();
        OrderedDrawCommandBlock small = pool.Take(4);
        OrderedDrawCommandBlock middle = pool.Take(8);
        OrderedDrawCommandBlock large = pool.Take(16);
        pool.Park(large);
        pool.Park(small);
        pool.Park(middle);

        Assert.Same(middle, pool.Take(5));
        Assert.Same(large, pool.Take(9));
        Assert.Same(small, pool.Take(4));
        Assert.Equal(3, pool.AllocationCount);
        Assert.Equal(3, pool.ReuseCount);
    }

    [Fact]
    public void ATakeLargerThanEverySetAllocatesAndLeavesTheSetsParked()
    {
        var pool = new OrderedDrawCommandBlockPool();
        OrderedDrawCommandBlock small = pool.Take(4);
        pool.Park(small);

        OrderedDrawCommandBlock big = pool.Take(5);

        Assert.NotSame(small, big);
        Assert.Equal(5, big.Capacity);
        Assert.Equal(2, pool.AllocationCount);
        Assert.Equal(4, pool.ParkedCapacity);
    }

    /// <summary>
    /// A set that outlived its producer carries whatever that producer wrote.
    /// The taker is handed an empty block, so nothing downstream -- every
    /// read of a block's contents is bounded by its count -- can see it.
    /// </summary>
    [Fact]
    public void ATakenSetCarriesNoContents()
    {
        var pool = new OrderedDrawCommandBlockPool();
        OrderedDrawCommandBlock first = pool.Take(4);
        first.Count = 4;
        pool.Park(first);

        Assert.Equal(0, pool.Take(2).Count);
    }

    /// <summary>
    /// The pool never keeps more commands parked than the most it has ever
    /// had live at once, so what it holds off a producer is at most a second
    /// copy of one generation, and the excess goes smallest first.
    /// </summary>
    [Fact]
    public void ParkedSetsStayInsideOneGenerationOfLiveCommands()
    {
        var pool = new OrderedDrawCommandBlockPool();
        pool.Park(pool.Take(10));
        Assert.Equal(10, pool.ParkedCapacity);

        // A taker that does not fit the parked set allocates its own. Parking
        // that one as well would keep two generations, so the smaller goes.
        pool.Park(pool.Take(11));

        Assert.Equal(11, pool.ParkedCapacity);
        Assert.Equal(1, pool.ParkedCount);

        // Two sets live at once is a larger generation, and then both fit.
        OrderedDrawCommandBlock first = pool.Take(11);
        OrderedDrawCommandBlock second = pool.Take(11);
        pool.Park(first);
        pool.Park(second);

        Assert.Equal(22, pool.ParkedCapacity);
        Assert.Equal(2, pool.ParkedCount);
    }

    [Fact]
    public void ParkingASetWithNoArraysKeepsNothing()
    {
        var pool = new OrderedDrawCommandBlockPool();

        pool.Park(new OrderedDrawCommandBlock());

        Assert.Equal(0, pool.ParkedCount);
        Assert.Equal(0, pool.ParkedCapacity);
    }

    [Fact]
    public void ExchangeParksTheOldSetAndServesTheNewOne()
    {
        var pool = new OrderedDrawCommandBlockPool();
        OrderedDrawCommandBlock first = pool.Take(4);
        OrderedDrawCommandBlock second = pool.Take(9);
        pool.Park(second);

        OrderedDrawCommandBlock grown = pool.Exchange(first, 9);

        Assert.Same(second, grown);
        Assert.Equal(4, pool.ParkedCapacity);
        Assert.Equal(2, pool.AllocationCount);
    }
}
