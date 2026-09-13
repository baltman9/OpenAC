using AcDream.UI.Abstractions.Settings;

namespace AcDream.App.Rendering.Gpu;

/// <summary>
/// How much device memory the backend sets aside up front: the pool block
/// size, the size from which an allocation gets a block of its own, the upload
/// staging ring, the per-frame ring, and the mesh arena's starting capacity.
/// Every pool still grows on demand; the profile only changes what is reserved
/// before the world asks for anything. The numbers are fixed for the life of
/// the device, so the graphics profile chooses one at startup.
/// </summary>
internal sealed record GpuMemoryProfile(
    string Name,
    ulong BlockSizeBytes,
    ulong DedicatedThresholdBytes,
    ulong StagingCapacityBytes,
    int RingCapacityBytesPerSlot,
    int MeshArenaInitialVertices,
    int MeshArenaInitialIndices)
{
    private const ulong MiB = 1024UL * 1024UL;

    /// <summary>One client that may use the whole GPU: 128 MiB blocks, 48 MiB
    /// of staging, 16 MiB per frame ring, and a 1M-vertex / 3M-index arena.</summary>
    public static GpuMemoryProfile Default { get; } = new(
        Name: "default",
        BlockSizeBytes: 128 * MiB,
        DedicatedThresholdBytes: 32 * MiB,
        StagingCapacityBytes: 48 * MiB,
        RingCapacityBytesPerSlot: 16 * 1024 * 1024,
        MeshArenaInitialVertices: 1024 * 1024,
        MeshArenaInitialIndices: 3 * 1024 * 1024);

    /// <summary>Many clients on one GPU (the Potato profile): 16 MiB blocks so
    /// a small world does not hold a 128 MiB block per memory type, 8 MiB of
    /// staging (a payload that does not fit takes a temporary buffer), 8 MiB
    /// per frame ring, and an arena that starts at a quarter of the default.</summary>
    public static GpuMemoryProfile Compact { get; } = new(
        Name: "compact",
        BlockSizeBytes: 16 * MiB,
        DedicatedThresholdBytes: 8 * MiB,
        StagingCapacityBytes: 8 * MiB,
        RingCapacityBytesPerSlot: 8 * 1024 * 1024,
        MeshArenaInitialVertices: 256 * 1024,
        MeshArenaInitialIndices: 1024 * 1024);

    /// <summary>The profile a graphics preset runs with: Potato is compact, every other preset default.</summary>
    public static GpuMemoryProfile For(QualityPreset preset) =>
        preset == QualityPreset.Potato ? Compact : Default;
}
