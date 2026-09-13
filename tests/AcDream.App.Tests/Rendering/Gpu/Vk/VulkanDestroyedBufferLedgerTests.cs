using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanDestroyedBufferLedgerTests
{
    [Fact]
    public void EverySlotSeesEachHandleOnce_AndTheSharedPrefixIsDropped()
    {
        var ledger = new VulkanDestroyedBufferLedger(slotCount: 2);
        ledger.Note(1); ledger.Note(2); ledger.Note(3);

        Assert.Equal([1ul, 2ul, 3ul], ledger.Take(0).ToArray());
        ledger.Note(4); ledger.Note(5);
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul], ledger.Take(1).ToArray());
        Assert.Equal(5, ledger.PendingCount); // slot 0 still owes 4 and 5

        ledger.Note(6);
        Assert.Equal([4ul, 5ul, 6ul], ledger.Take(0).ToArray());
        Assert.Equal(3, ledger.PendingCount); // 1..3 consumed by both, dropped

        Assert.Equal([6ul], ledger.Take(1).ToArray());
        Assert.Equal(1, ledger.PendingCount);

        Assert.True(ledger.Take(0).IsEmpty);
        Assert.Equal(0, ledger.PendingCount);
        Assert.True(ledger.Take(1).IsEmpty);
    }

    [Fact]
    public void ASlotBehindTheOthers_TakesEverythingSinceItsOwnCursor()
    {
        // Slot 0 consumed five handles before slot 1 ran at all; slot 1's
        // take must start at its own (zero) cursor, not at slot 0's.
        var ledger = new VulkanDestroyedBufferLedger(slotCount: 2);
        for (ulong handle = 1; handle <= 5; handle++)
            ledger.Note(handle);

        Assert.Equal(5, ledger.Take(0).Length);
        Assert.Equal([1ul, 2ul, 3ul, 4ul, 5ul], ledger.Take(1).ToArray());
        Assert.Equal(5, ledger.PendingCount);

        Assert.True(ledger.Take(0).IsEmpty);
        Assert.Equal(0, ledger.PendingCount);
    }

    [Fact]
    public void StreamingNeverGrowsBeyondTheFramesInFlight()
    {
        // Something is destroyed every frame; the list must stay bounded by
        // what the slots in flight have not yet consumed.
        var ledger = new VulkanDestroyedBufferLedger(slotCount: 3);
        ulong next = 1;
        for (int frame = 0; frame < 300; frame++)
        {
            ledger.Note(next++);
            ledger.Note(next++);
            // A slot sees the three frames destroyed since its own last take.
            Assert.Equal(2 * Math.Min(frame + 1, 3), ledger.Take(frame % 3).Length);
            Assert.True(ledger.PendingCount <= 2 * 3, $"frame {frame}: {ledger.PendingCount} pending");
        }
    }

    [Fact]
    public void ASlotThatNeverRuns_HoldsTheListButNothingIsLost()
    {
        var ledger = new VulkanDestroyedBufferLedger(slotCount: 2);
        ledger.Note(7);
        Assert.Equal([7ul], ledger.Take(0).ToArray());
        Assert.True(ledger.Take(0).IsEmpty);
        Assert.Equal(1, ledger.PendingCount);
        Assert.Equal([7ul], ledger.Take(1).ToArray());
    }
}
