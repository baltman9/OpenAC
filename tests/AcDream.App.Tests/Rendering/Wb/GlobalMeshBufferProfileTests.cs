using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Rendering.Wb;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Content;

namespace AcDream.App.Tests.Rendering.Wb;

public sealed class GlobalMeshBufferProfileTests
{
    [Fact]
    public void Arena_StartsAtTheRequestedCapacity_AndDefaultsToTheDefaultProfile()
    {
        using var device = new RecordingGpuDevice();
        GpuMemoryProfile compact = GpuMemoryProfile.Compact;

        using var standard = new GlobalMeshBuffer(
            device,
            ImmediateGpuResourceRetirementQueue.Instance);
        using var small = new GlobalMeshBuffer(
            device,
            ImmediateGpuResourceRetirementQueue.Instance,
            compact.MeshArenaInitialVertices,
            compact.MeshArenaInitialIndices);

        Assert.Equal(
            (long)GpuMemoryProfile.Default.MeshArenaInitialVertices * VertexPositionNormalTexture.Size
            + (long)GpuMemoryProfile.Default.MeshArenaInitialIndices * sizeof(ushort),
            standard.CapacityBytes);
        Assert.Equal(
            (long)compact.MeshArenaInitialVertices * VertexPositionNormalTexture.Size
            + (long)compact.MeshArenaInitialIndices * sizeof(ushort),
            small.CapacityBytes);
        Assert.True(small.HasStores);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(int.MaxValue, 1)]
    public void Arena_RejectsAnEmptyOrOversizedStart(int vertices, int indices)
    {
        using var device = new RecordingGpuDevice();

        Assert.Throws<ArgumentOutOfRangeException>(() => new GlobalMeshBuffer(
            device,
            ImmediateGpuResourceRetirementQueue.Instance,
            vertices,
            indices));
    }
}
