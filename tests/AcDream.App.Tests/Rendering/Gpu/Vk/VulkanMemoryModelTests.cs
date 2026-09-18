using System;
using System.Collections.Generic;
using System.Threading;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Gpu.Vk;
using Silk.NET.Vulkan;

namespace AcDream.App.Tests.Rendering.Gpu.Vk;

public sealed class VulkanMemoryModelTests
{
    // ── free list ────────────────────────────────────────────────────────────

    [Fact]
    public void FreeList_AllocatesFromTheFrontAndTracksUse()
    {
        var block = new VulkanMemoryBlockFreeList(1024);

        Assert.True(block.TryAllocate(256, 1, out ulong first));
        Assert.True(block.TryAllocate(256, 1, out ulong second));

        Assert.Equal(0ul, first);
        Assert.Equal(256ul, second);
        Assert.Equal(512ul, block.UsedBytes);
        Assert.Equal(512ul, block.FreeBytes);
    }

    [Fact]
    public void FreeList_HonoursAlignmentAndChargesThePaddingToTheAllocation()
    {
        var block = new VulkanMemoryBlockFreeList(1024);
        Assert.True(block.TryAllocate(1, 1, out _));

        Assert.True(block.TryAllocate(16, 256, out ulong aligned));

        Assert.Equal(256ul, aligned);
        // 1 byte of payload, then 255 bytes of padding plus 16 of payload.
        Assert.Equal(272ul, block.UsedBytes);
    }

    [Fact]
    public void FreeList_ReleasingAnAlignedAllocationReturnsItsPaddingToo()
    {
        var block = new VulkanMemoryBlockFreeList(1024);
        Assert.True(block.TryAllocate(1, 1, out _));
        Assert.True(block.TryAllocate(16, 256, out ulong aligned));

        block.Free(aligned, 16);

        // Only the leading 1-byte allocation is still out.
        Assert.Equal(1ul, block.UsedBytes);
        Assert.Equal(1023ul, block.FreeBytes);
    }

    [Fact]
    public void FreeList_CoalescesNeighboursSoTheBlockDoesNotFragmentAway()
    {
        var block = new VulkanMemoryBlockFreeList(1024);
        Assert.True(block.TryAllocate(256, 1, out ulong a));
        Assert.True(block.TryAllocate(256, 1, out ulong b));
        Assert.True(block.TryAllocate(256, 1, out ulong c));

        block.Free(a, 256);
        block.Free(c, 256);
        // Two ranges, not three: releasing c already merged with the block's
        // untouched tail.
        Assert.Equal(2, block.FreeRangeCount);

        block.Free(b, 256);

        Assert.Equal(1, block.FreeRangeCount);
        Assert.Equal(1024ul, block.LargestFreeBytes);
        Assert.Equal(0ul, block.UsedBytes);
    }

    [Fact]
    public void FreeList_RefusesAnAllocationLargerThanTheLargestHole()
    {
        var block = new VulkanMemoryBlockFreeList(1024);
        Assert.True(block.TryAllocate(600, 1, out _));

        Assert.False(block.TryAllocate(600, 1, out _));
        Assert.True(block.TryAllocate(400, 1, out _));
    }

    [Fact]
    public void FreeList_DoubleReleaseIsRejectedRatherThanCorruptingTheAccounting()
    {
        var block = new VulkanMemoryBlockFreeList(1024);
        Assert.True(block.TryAllocate(256, 1, out ulong offset));
        block.Free(offset, 256);

        Assert.Throws<InvalidOperationException>(() => block.Free(offset, 256));
    }

    // ── pool policy ──────────────────────────────────────────────────────────

    [Fact]
    public void Pool_AsksForANewBlockBeforeItHasOne()
    {
        var pool = new VulkanMemoryTypePool(memoryTypeIndex: 0, blockSizeBytes: 4096, dedicatedThresholdBytes: 2048);

        Assert.False(pool.TryAllocate(64, 1, out _));

        int index = pool.AddBlock(pool.BlockCapacityFor(64), dedicated: false);
        Assert.Equal(0, index);
        Assert.True(pool.TryAllocate(64, 1, out VulkanMemoryRange range));
        Assert.Equal(0, range.BlockIndex);
        Assert.False(range.IsDedicated);
    }

    [Fact]
    public void Pool_GivesALargeRequestAWholeBlockOfItsOwn()
    {
        var pool = new VulkanMemoryTypePool(memoryTypeIndex: 0, blockSizeBytes: 4096, dedicatedThresholdBytes: 2048);
        int shared = pool.AddBlock(4096, dedicated: false);
        Assert.Equal(0, shared);

        // 3000 >= the 2048 threshold, so it must not be placed in the shared
        // block even though the shared block would fit it — taking that space
        // would strand the remaining kilobyte.
        Assert.False(pool.TryAllocate(3000, 1, out _));
        Assert.Equal(3000ul, pool.BlockCapacityFor(3000));

        int dedicated = pool.AddBlock(3000, dedicated: true);
        VulkanMemoryRange range = pool.AllocateWholeBlock(dedicated, 3000);

        Assert.True(range.IsDedicated);
        Assert.Equal(0ul, range.OffsetBytes);
        Assert.Equal(2, pool.LiveBlockCount);
    }

    [Fact]
    public void Pool_RetiresAnyBlockThatEmpties_DedicatedOrShared()
    {
        var pool = new VulkanMemoryTypePool(memoryTypeIndex: 0, blockSizeBytes: 4096, dedicatedThresholdBytes: 2048);
        int shared = pool.AddBlock(4096, dedicated: false);
        Assert.True(pool.TryAllocate(64, 1, out VulkanMemoryRange small));
        Assert.True(pool.TryAllocate(64, 1, out VulkanMemoryRange alsoSmall));
        int dedicatedBlock = pool.AddBlock(3000, dedicated: true);
        VulkanMemoryRange large = pool.AllocateWholeBlock(dedicatedBlock, 3000);

        Assert.True(pool.Free(large));       // dedicated block: nothing else can use it
        Assert.False(pool.Free(small));      // shared block still holds the other allocation
        Assert.True(pool.Free(alsoSmall));   // now empty, so it goes back to the driver

        Assert.Equal(0, shared);
        Assert.Equal(0, pool.LiveBlockCount);
    }

    [Fact]
    public void Pool_ReusesTheHoleAnAllocationLeavesWhileTheBlockIsStillInUse()
    {
        var pool = new VulkanMemoryTypePool(memoryTypeIndex: 3, blockSizeBytes: 1024, dedicatedThresholdBytes: 4096);
        pool.AddBlock(1024, dedicated: false);
        Assert.True(pool.TryAllocate(512, 1, out VulkanMemoryRange first));
        Assert.True(pool.TryAllocate(512, 1, out _));
        Assert.False(pool.TryAllocate(16, 1, out _));

        Assert.False(pool.Free(first));

        Assert.True(pool.TryAllocate(16, 1, out VulkanMemoryRange reused));
        Assert.Equal(0, reused.BlockIndex);
        Assert.Equal(1, pool.LiveBlockCount);
    }

    [Fact]
    public void Pool_NeverHandsOutTheIndexOfARetiredBlock()
    {
        // A range carries its block index, and a stale index must not be able
        // to name a different block's memory.
        var pool = new VulkanMemoryTypePool(memoryTypeIndex: 0, blockSizeBytes: 1024, dedicatedThresholdBytes: 4096);
        pool.AddBlock(1024, dedicated: false);
        Assert.True(pool.TryAllocate(1024, 1, out VulkanMemoryRange whole));
        Assert.True(pool.Free(whole));
        Assert.Equal(0, pool.LiveBlockCount);

        int replacement = pool.AddBlock(1024, dedicated: false);

        Assert.Equal(1, replacement);
        Assert.True(pool.TryAllocate(16, 1, out VulkanMemoryRange range));
        Assert.Equal(1, range.BlockIndex);
    }

    // ── heap selection ───────────────────────────────────────────────────────

    private static readonly MemoryPropertyFlags DeviceLocal = MemoryPropertyFlags.DeviceLocalBit;
    private static readonly MemoryPropertyFlags HostCoherent =
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
    private static readonly MemoryPropertyFlags ReBar =
        MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
    private static readonly MemoryPropertyFlags HostCached =
        MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit;

    [Fact]
    public void Selection_HostWritablePrefersResizableBarOverPlainHostMemory()
    {
        List<MemoryPropertyFlags> types = [DeviceLocal, HostCoherent, ReBar];

        uint? chosen = VulkanMemoryTypeSelection.Choose(types, 0b111, GpuMemoryResidency.HostWritable);

        Assert.Equal(2u, chosen);
    }

    [Fact]
    public void Selection_HostWritableFallsBackWhenNoResizableBarTypeExists()
    {
        List<MemoryPropertyFlags> types = [DeviceLocal, HostCoherent];

        Assert.Equal(1u, VulkanMemoryTypeSelection.Choose(types, 0b11, GpuMemoryResidency.HostWritable));
    }

    [Fact]
    public void Selection_RespectsTheResourcesAllowedTypeMask()
    {
        List<MemoryPropertyFlags> types = [ReBar, HostCoherent];

        // Bit 0 is masked out even though it is the preferred type.
        Assert.Equal(1u, VulkanMemoryTypeSelection.Choose(types, 0b10, GpuMemoryResidency.HostWritable));
    }

    [Fact]
    public void Selection_DeviceLocalPrefersDeviceLocalButAcceptsAnythingRatherThanFailing()
    {
        List<MemoryPropertyFlags> deviceLocalPresent = [HostCoherent, DeviceLocal];
        Assert.Equal(1u, VulkanMemoryTypeSelection.Choose(deviceLocalPresent, 0b11, GpuMemoryResidency.DeviceLocal));

        List<MemoryPropertyFlags> unifiedMemory = [HostCoherent];
        Assert.Equal(0u, VulkanMemoryTypeSelection.Choose(unifiedMemory, 0b1, GpuMemoryResidency.DeviceLocal));
    }

    [Fact]
    public void Selection_HostReadablePrefersCachedMemoryForTheReadbackItExistsFor()
    {
        List<MemoryPropertyFlags> types = [HostCoherent, HostCached];

        Assert.Equal(1u, VulkanMemoryTypeSelection.Choose(types, 0b11, GpuMemoryResidency.HostReadable));
    }

    [Fact]
    public void Selection_AnswersNullWhenNothingIsUsableRatherThanGuessing()
    {
        List<MemoryPropertyFlags> types = [DeviceLocal];

        Assert.Null(VulkanMemoryTypeSelection.Choose(types, 0b1, GpuMemoryResidency.HostWritable));
    }

    [Fact]
    public void Selection_NamesWhichResidenciesMustBeMappable()
    {
        Assert.False(VulkanMemoryTypeSelection.RequiresMapping(GpuMemoryResidency.DeviceLocal));
        Assert.True(VulkanMemoryTypeSelection.RequiresMapping(GpuMemoryResidency.HostWritable));
        Assert.True(VulkanMemoryTypeSelection.RequiresMapping(GpuMemoryResidency.HostReadable));
    }

    [Fact]
    public void Selection_HostWritableListsEveryUsableTypeInPreferenceOrderWithoutRepeats()
    {
        // Index 1 satisfies the ReBAR mask AND the plain host-visible masks that
        // follow it, so a naive "append every pass" would list it three times and
        // the fallback would retry a type the device has already refused.
        List<MemoryPropertyFlags> types = [DeviceLocal, ReBar, HostCoherent, HostCached];

        IReadOnlyList<uint> all = VulkanMemoryTypeSelection.ChooseAll(types, 0b1111, GpuMemoryResidency.HostWritable);

        Assert.Equal([1u, 2u, 3u], all);
        Assert.Equal(1u, VulkanMemoryTypeSelection.Choose(types, 0b1111, GpuMemoryResidency.HostWritable));
    }

    // ── allocator fallback across memory types ───────────────────────────────

    private sealed class FakeDeviceMemoryBackend : IVulkanDeviceMemoryBackend
    {
        private readonly Dictionary<uint, Result> _refusals = [];
        private ulong _nextHandle = 1;
        private nint _nextPointer = 0x1000;

        internal FakeDeviceMemoryBackend(params (uint TypeIndex, Result Result)[] refusals)
        {
            foreach ((uint typeIndex, Result result) in refusals)
                _refusals[typeIndex] = result;
        }

        /// <summary>Memory type of every vkAllocateMemory attempt, in order.</summary>
        internal List<uint> AllocateCalls { get; } = [];

        internal List<DeviceMemory> FreeCalls { get; } = [];

        internal List<DeviceMemory> UnmapCalls { get; } = [];

        /// <summary>Signalled as the fake driver enters vkFreeMemory.</summary>
        internal ManualResetEventSlim FreeEntered { get; } = new(false);

        /// <summary>Holds the fake driver inside vkFreeMemory until it is set.</summary>
        internal ManualResetEventSlim? FreeGate { get; set; }

        /// <summary>Runs inside vkFreeMemory, before it returns.</summary>
        internal Action? WhileFreeing { get; set; }

        public Result AllocateMemory(uint typeIndex, ulong sizeBytes, out DeviceMemory memory)
        {
            AllocateCalls.Add(typeIndex);
            if (_refusals.TryGetValue(typeIndex, out Result refusal))
            {
                memory = default;
                return refusal;
            }

            memory = new DeviceMemory(_nextHandle++);
            return Result.Success;
        }

        public Result MapMemory(DeviceMemory memory, ulong sizeBytes, out nint pointer)
        {
            pointer = _nextPointer;
            _nextPointer += 0x10000;
            return Result.Success;
        }

        public void UnmapMemory(DeviceMemory memory)
        {
            lock (UnmapCalls)
                UnmapCalls.Add(memory);
        }

        public void FreeMemory(DeviceMemory memory)
        {
            FreeEntered.Set();
            WhileFreeing?.Invoke();
            FreeGate?.Wait(TimeSpan.FromSeconds(5));
            lock (FreeCalls)
                FreeCalls.Add(memory);
        }
    }

    private static readonly MemoryRequirements SmallHostRequirement = new()
    {
        Size = 64,
        Alignment = 16,
        MemoryTypeBits = 0b1111,
    };

    private static VulkanDeviceMemoryAllocator NewAllocator(FakeDeviceMemoryBackend backend) =>
        new(
            backend,
            [DeviceLocal, ReBar, HostCoherent, HostCached],
            blockSizeBytes: 4096,
            dedicatedThresholdBytes: 8192);

    [Fact]
    public void Allocator_FallsBackToTheNextHostVisibleTypeWhenTheDeviceRefusesTheFirst()
    {
        var backend = new FakeDeviceMemoryBackend((1u, Result.ErrorOutOfDeviceMemory));
        using VulkanDeviceMemoryAllocator allocator = NewAllocator(backend);

        VulkanAllocation allocation = allocator.Allocate(
            SmallHostRequirement,
            GpuMemoryResidency.HostWritable,
            "vk-staging-ring");

        Assert.Equal(2u, allocation.MemoryTypeIndex);
        Assert.True(allocation.IsMapped);
        Assert.Equal(1, allocator.DeviceMemoryObjectCount);
        Assert.Equal(4096ul, allocator.CommittedBytes);
        // The refused type left nothing behind: one live block, on type 2.
        Assert.Equal(1, allocator.LiveBlockCount);
        Assert.Equal([1u, 2u], backend.AllocateCalls);
    }

    [Fact]
    public void Allocator_NeverRetriesAMemoryTypeTheDeviceAlreadyRefused()
    {
        var backend = new FakeDeviceMemoryBackend((1u, Result.ErrorOutOfDeviceMemory));
        using VulkanDeviceMemoryAllocator allocator = NewAllocator(backend);
        _ = allocator.Allocate(SmallHostRequirement, GpuMemoryResidency.HostWritable, "first");

        var second = new MemoryRequirements { Size = 4096, Alignment = 16, MemoryTypeBits = 0b1111 };
        VulkanAllocation allocation = allocator.Allocate(second, GpuMemoryResidency.HostWritable, "second");

        Assert.Equal(2u, allocation.MemoryTypeIndex);
        Assert.Equal([1u, 2u, 2u], backend.AllocateCalls);
    }

    [Fact]
    public void Allocator_HandsAnEmptiedSharedBlockBackToTheDriver()
    {
        // The reason this slice exists: a staging temporary is a share of a
        // block, so if a shared block is never returned the bytes a burst
        // needed stay committed and mapped for the rest of the session.
        var backend = new FakeDeviceMemoryBackend();
        using VulkanDeviceMemoryAllocator allocator = NewAllocator(backend);
        VulkanAllocation first = allocator.Allocate(SmallHostRequirement, GpuMemoryResidency.HostWritable, "temp-a");
        VulkanAllocation second = allocator.Allocate(SmallHostRequirement, GpuMemoryResidency.HostWritable, "temp-b");
        Assert.Equal(1, allocator.LiveBlockCount);

        allocator.Free(first);
        allocator.DrainBlockReleases();
        Assert.Empty(backend.FreeCalls);
        Assert.Equal(4096ul, allocator.CommittedBytes);

        allocator.Free(second);
        allocator.DrainBlockReleases();

        Assert.Single(backend.FreeCalls);
        Assert.Single(backend.UnmapCalls);
        Assert.Equal(0, allocator.LiveBlockCount);
        Assert.Equal(0, allocator.DeviceMemoryObjectCount);
        Assert.Equal(0ul, allocator.CommittedBytes);
        Assert.Equal(0ul, allocator.AllocatedBytes);
    }

    [Fact]
    public void Allocator_StillCountsABlockWhoseDriverFreeHasNotReturned()
    {
        // The free is asynchronous, so the accounting has to say the process
        // still owns the block until the driver call comes back — otherwise
        // the memory report understates what is held.
        var gate = new ManualResetEventSlim(false);
        var backend = new FakeDeviceMemoryBackend { FreeGate = gate };
        using VulkanDeviceMemoryAllocator allocator = NewAllocator(backend);
        VulkanAllocation only = allocator.Allocate(SmallHostRequirement, GpuMemoryResidency.HostWritable, "temp");

        allocator.Free(only);
        Assert.True(backend.FreeEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, allocator.DeviceMemoryObjectCount);
        Assert.Equal(4096ul, allocator.CommittedBytes);
        Assert.Equal(0ul, allocator.AllocatedBytes);

        gate.Set();
        allocator.DrainBlockReleases();

        Assert.Equal(0, allocator.DeviceMemoryObjectCount);
        Assert.Equal(0ul, allocator.CommittedBytes);
    }

    [Fact]
    public void Allocator_DoesNotHoldItsLockWhileTheDriverFreesABlock()
    {
        // A mapped 32 MiB block takes the best part of a millisecond to unmap
        // and free. If that ran under the allocator's lock it would stall the
        // next allocation on the frame thread, which is the hitch this slice
        // is about.
        var backend = new FakeDeviceMemoryBackend();
        using VulkanDeviceMemoryAllocator allocator = NewAllocator(backend);
        VulkanAllocation only = allocator.Allocate(SmallHostRequirement, GpuMemoryResidency.HostWritable, "temp");

        // The attempt runs from inside the driver's free, whichever thread is
        // making that call, so the pin holds however the release is scheduled.
        var allocated = new ManualResetEventSlim(false);
        Thread? allocating = null;
        bool allocatedWhileFreeing = false;
        backend.WhileFreeing = () =>
        {
            // Once: the allocator's own teardown frees blocks too, and by then
            // allocating from it is an error.
            backend.WhileFreeing = null;
            allocating = new Thread(() =>
            {
                _ = allocator.Allocate(SmallHostRequirement, GpuMemoryResidency.HostWritable, "next");
                allocated.Set();
            });
            allocating.Start();
            allocatedWhileFreeing = allocated.Wait(TimeSpan.FromSeconds(2));
        };

        allocator.Free(only);
        allocator.DrainBlockReleases();
        allocating?.Join(TimeSpan.FromSeconds(5));

        Assert.True(
            allocatedWhileFreeing,
            "an allocation waited on a block free that had already left the allocator's tables");
    }

    [Fact]
    public void Allocator_FailsWithEveryTriedTypeNamedWhenNoHostVisibleTypeCanBeBacked()
    {
        var backend = new FakeDeviceMemoryBackend(
            (1u, Result.ErrorOutOfDeviceMemory),
            (2u, Result.ErrorOutOfDeviceMemory),
            (3u, Result.ErrorOutOfHostMemory));
        using VulkanDeviceMemoryAllocator allocator = NewAllocator(backend);

        VulkanCallException failure = Assert.Throws<VulkanCallException>(
            () => allocator.Allocate(SmallHostRequirement, GpuMemoryResidency.HostWritable, "vk-staging-ring"));

        Assert.Contains("1", failure.Message);
        Assert.Contains("2", failure.Message);
        Assert.Contains("3", failure.Message);
        Assert.Contains("vk-staging-ring", failure.Message);
        Assert.Equal(Result.ErrorOutOfHostMemory, failure.Result);
        Assert.Equal([1u, 2u, 3u], backend.AllocateCalls);
        // A refused block must not be booked: the pool is exactly as it started.
        Assert.Equal(0, allocator.DeviceMemoryObjectCount);
        Assert.Equal(0, allocator.LiveBlockCount);
        Assert.Equal(0ul, allocator.CommittedBytes);
    }

    [Fact]
    public void Allocator_PropagatesANonMemoryFailureInsteadOfWalkingTheRestOfTheTypes()
    {
        // Out-of-memory is a fact about one heap; a lost device is a fact about
        // the whole device, and trying the next type would only hide it.
        var backend = new FakeDeviceMemoryBackend((1u, Result.ErrorDeviceLost));
        using VulkanDeviceMemoryAllocator allocator = NewAllocator(backend);

        VulkanCallException failure = Assert.Throws<VulkanCallException>(
            () => allocator.Allocate(SmallHostRequirement, GpuMemoryResidency.HostWritable, "vk-staging-ring"));

        Assert.Equal(Result.ErrorDeviceLost, failure.Result);
        Assert.Equal([1u], backend.AllocateCalls);
        Assert.Equal(0, allocator.DeviceMemoryObjectCount);
    }
}
