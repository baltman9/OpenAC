using AcDream.App.Rendering.Gpu;
using AcDream.UI.Abstractions.Settings;

namespace AcDream.App.Tests.Rendering.Gpu;

public sealed class GpuMemoryProfileTests
{
    [Theory]
    [InlineData(QualityPreset.Low)]
    [InlineData(QualityPreset.Medium)]
    [InlineData(QualityPreset.High)]
    [InlineData(QualityPreset.Ultra)]
    public void EveryPresetButPotato_RunsWithTheDefaultProfile(QualityPreset preset)
    {
        Assert.Same(GpuMemoryProfile.Default, GpuMemoryProfile.For(preset));
    }

    [Fact]
    public void Potato_RunsWithTheCompactProfile()
    {
        Assert.Same(GpuMemoryProfile.Compact, GpuMemoryProfile.For(QualityPreset.Potato));
    }

    [Fact]
    public void Compact_ReservesLessThanDefault_ExceptTheFrameRing_AndBothKeepDedicatedBelowBlock()
    {
        GpuMemoryProfile compact = GpuMemoryProfile.Compact;
        GpuMemoryProfile standard = GpuMemoryProfile.Default;

        Assert.True(compact.BlockSizeBytes < standard.BlockSizeBytes);
        Assert.True(compact.DedicatedThresholdBytes < standard.DedicatedThresholdBytes);
        Assert.True(compact.StagingCapacityBytes < standard.StagingCapacityBytes);
        // The per-frame ring is the one pool without a fallback; it stays.
        Assert.Equal(standard.RingCapacityBytesPerSlot, compact.RingCapacityBytesPerSlot);
        Assert.True(compact.MeshArenaInitialVertices < standard.MeshArenaInitialVertices);
        Assert.True(compact.MeshArenaInitialIndices < standard.MeshArenaInitialIndices);

        foreach (GpuMemoryProfile profile in new[] { compact, standard })
        {
            Assert.True(profile.DedicatedThresholdBytes <= profile.BlockSizeBytes);
            Assert.True(profile.RingCapacityBytesPerSlot > 0);
            Assert.True(profile.StagingCapacityBytes > 0);
        }

        // The numbers both profiles were measured with; a change here is a
        // deliberate re-measure, not a drift.
        Assert.Equal(32UL * 1024 * 1024, standard.BlockSizeBytes);
        Assert.Equal(16UL * 1024 * 1024, standard.DedicatedThresholdBytes);
        Assert.Equal(48UL * 1024 * 1024, standard.StagingCapacityBytes);
        Assert.Equal(16UL * 1024 * 1024, compact.BlockSizeBytes);
        Assert.Equal(8UL * 1024 * 1024, compact.DedicatedThresholdBytes);
        Assert.Equal(8UL * 1024 * 1024, compact.StagingCapacityBytes);
        Assert.Equal(16 * 1024 * 1024, compact.RingCapacityBytesPerSlot);
        Assert.Equal(256 * 1024, compact.MeshArenaInitialVertices);
        Assert.Equal(1024 * 1024, compact.MeshArenaInitialIndices);
    }
}
