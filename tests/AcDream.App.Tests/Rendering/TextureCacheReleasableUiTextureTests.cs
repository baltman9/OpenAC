using System.Collections.Generic;
using System.Linq;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;

namespace AcDream.App.Tests.Rendering;

/// <summary>
/// The interface's ad-hoc uploads are kept for the life of the cache. The
/// releasable path is the one exception: an upload that is given back one
/// handle at a time, with the GPU work deferred through the retirement
/// queue because a frame still in flight can be sampling the texture.
/// </summary>
public sealed class TextureCacheReleasableUiTextureTests
{
    private sealed class HeldRetirementQueue : IGpuResourceRetirementQueue
    {
        public List<Action> Pending { get; } = [];

        public void Retire(Action release) => Pending.Add(release);

        public void RunAll()
        {
            foreach (Action release in Pending) release();
            Pending.Clear();
        }
    }

    private static (RecordingGpuDevice Device, TextureCache Cache, HeldRetirementQueue Queue) Build()
    {
        var device = new RecordingGpuDevice();
        var queue = new HeldRetirementQueue();
        var cache = new TextureCache(
            device,
            dats: null!,
            queue,
            Path.Combine(Path.GetTempPath(), "acdream-tests", "releasable"));
        device.Clear();
        return (device, cache, queue);
    }

    [Fact]
    public void ReleaseForgetsTheHandleNowAndFreesTheTextureOnlyWhenTheQueueSays()
    {
        (RecordingGpuDevice device, TextureCache cache, HeldRetirementQueue queue) = Build();
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
        (_, TextureCache cache, HeldRetirementQueue queue) = Build();
        uint handle = cache.UploadReleasableRgba8(new byte[4 * 4 * 4], 4, 4, "test-image");

        Assert.True(cache.ReleaseUiTexture(handle));
        Assert.False(cache.ReleaseUiTexture(handle));
        Assert.Single(queue.Pending);
    }

    [Fact]
    public void AHandleFromTheAdHocPathCannotBeReleasedThroughIt()
    {
        (_, TextureCache cache, HeldRetirementQueue queue) = Build();
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

        cache.Dispose();

        Assert.Contains(
            device.OfKind<GpuRecordedTextureRelease>(),
            call => call.Slot == UiTextureTableHandle.ToSlot(handle).Index);
    }
}
