using System.Collections.Generic;
using System.Linq;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// The interface's ad-hoc uploads are kept for the life of the cache. The
/// releasable path is the one exception: an upload that is given back one
/// handle at a time. The handle is forgotten at once; the GPU work waits
/// for the frames in flight inside the device, which is the one owner of
/// that wait, so the cache asks the device at once and defers nothing of
/// its own.
/// </summary>
public sealed class TextureCacheReleasableUiTextureTests
{
    private static (RecordingGpuDevice Device, TextureCache Cache, HeldGpuRetirementQueue Queue) Build()
    {
        var queue = new HeldGpuRetirementQueue();
        var device = new RecordingGpuDevice(retirement: queue);
        var cache = new TextureCache(
            device,
            dats: null!,
            device.Retirement,
            Path.Combine(Path.GetTempPath(), "acdream-tests", "releasable"));
        device.Clear();
        return (device, cache, queue);
    }

    [Fact]
    public void ReleaseForgetsTheHandleNowAndFreesTheTextureOnlyWhenTheQueueSays()
    {
        (RecordingGpuDevice device, TextureCache cache, HeldGpuRetirementQueue queue) = Build();
        uint handle = cache.UploadReleasableRgba8(new byte[4 * 4 * 4], 4, 4, "test-image");
        Assert.NotEqual(0u, handle);
        Assert.Equal(1, cache.ReleasableUiTextureCount);
        int slotsBefore = device.LiveTextureSlotCount;

        Assert.True(cache.ReleaseUiTexture(handle));

        Assert.Equal(0, cache.ReleasableUiTextureCount);
        Assert.Single(queue.Pending);
        Assert.Equal(slotsBefore, device.LiveTextureSlotCount);
        Assert.Empty(device.OfKind<GpuRecordedTextureRelease>());

        queue.RunAll();

        Assert.Equal(slotsBefore - 1, device.LiveTextureSlotCount);
        GpuRecordedTextureRelease released = Assert.Single(device.OfKind<GpuRecordedTextureRelease>());
        Assert.Equal(UiTextureTableHandle.ToSlot(handle).Index, released.Slot);
    }

    [Fact]
    public void ASecondReleaseOfTheSameHandleIsRefused()
    {
        (_, TextureCache cache, HeldGpuRetirementQueue queue) = Build();
        uint handle = cache.UploadReleasableRgba8(new byte[4 * 4 * 4], 4, 4, "test-image");

        Assert.True(cache.ReleaseUiTexture(handle));
        Assert.False(cache.ReleaseUiTexture(handle));
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void AHandleFromTheAdHocPathCannotBeReleasedThroughIt()
    {
        (_, TextureCache cache, HeldGpuRetirementQueue queue) = Build();
        uint adhoc = cache.UploadRgba8(new byte[4 * 4 * 4], 4, 4);

        Assert.False(cache.ReleaseUiTexture(adhoc));
        Assert.False(cache.ReleaseUiTexture(0u));
        Assert.Empty(queue.Pending);
    }

    [Fact]
    public void DisposeFreesWhatWasNeverReleased()
    {
        (RecordingGpuDevice device, TextureCache cache, _) = Build();
        uint handle = cache.UploadReleasableRgba8(new byte[4 * 4 * 4], 4, 4, "test-image");

        // The client's order: the cache goes, then the device drains what
        // the cache gave back to it.
        cache.Dispose();
        device.Dispose();

        Assert.Contains(
            device.OfKind<GpuRecordedTextureRelease>(),
            call => call.Slot == UiTextureTableHandle.ToSlot(handle).Index);
    }
}
