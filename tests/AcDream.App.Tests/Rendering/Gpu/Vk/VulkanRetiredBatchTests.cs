using System;
using System.Collections.Generic;
using AcDream.App.Rendering.Gpu.Vk;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanRetiredBatchTests
{
    [Fact]
    public void ABatchReleasedTwiceReleasesOnce()
    {
        // The flight ledger's closure keeps hold of the batch and teardown
        // drains that ledger more than once. A second pass over a batch whose
        // buffers are already destroyed would destroy dead handles and hand
        // ranges back to a block that has been retired, so it must do nothing.
        List<string> batch = ["a", "b", "c"];
        var released = new List<string>();

        VulkanRetiredBatch.DrainOnce(batch, released.Add);
        VulkanRetiredBatch.DrainOnce(batch, released.Add);

        Assert.Equal(["a", "b", "c"], released);
        Assert.Empty(batch);
    }

    [Fact]
    public void EveryEntryIsReleasedInOrderBeforeTheBatchIsEmptied()
    {
        List<int> batch = [1, 2, 3, 4];
        var released = new List<int>();

        VulkanRetiredBatch.DrainOnce(batch, entry =>
        {
            // The batch still holds everything while a release is running: a
            // release that reads it must not see a half-emptied list.
            Assert.Equal(4, batch.Count);
            released.Add(entry);
        });

        Assert.Equal([1, 2, 3, 4], released);
        Assert.Empty(batch);
    }

    [Fact]
    public void AReleaseThatThrowsLeavesTheRestOfTheBatchToBeReleased()
    {
        // Dropping them would leak the memory they hold; keeping them lets the
        // failure be the loud thing it is without losing the rest.
        List<int> batch = [1, 2, 3];
        var released = new List<int>();

        Assert.Throws<InvalidOperationException>(() =>
            VulkanRetiredBatch.DrainOnce<int>(batch, entry =>
            {
                if (entry == 2)
                    throw new InvalidOperationException("the driver refused");
                released.Add(entry);
            }));

        Assert.Equal([1], released);
        Assert.Equal([1, 2, 3], batch);
    }
}
