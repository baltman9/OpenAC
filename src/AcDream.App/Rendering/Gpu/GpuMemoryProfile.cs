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

    /// <summary>One client that may use the whole GPU: 32 MiB blocks, 48 MiB
    /// of staging, 16 MiB per frame ring, and a 1M-vertex / 3M-index arena.
    /// <para>A block is committed whole, and on a host-visible memory type it
    /// is mapped whole, so it costs the process address space as well as the
    /// device. Measured at three fixed spots, 128 MiB blocks left 328 MiB of
    /// the 1,091 MiB committed unused, three of them under a third full.
    /// Anything at or above the dedicated threshold takes a block sized exactly
    /// to it instead of a share of one, so the large textures, vertex stores
    /// and frame rings waste nothing and hand everything back when they are
    /// released.</para></summary>
    public static GpuMemoryProfile Default { get; } = new(
        Name: "default",
        BlockSizeBytes: 32 * MiB,
        DedicatedThresholdBytes: 16 * MiB,
        StagingCapacityBytes: 48 * MiB,
        RingCapacityBytesPerSlot: 16 * 1024 * 1024,
        MeshArenaInitialVertices: 1024 * 1024,
        MeshArenaInitialIndices: 3 * 1024 * 1024);

    /// <summary>Many clients on one GPU (the Potato profile): 16 MiB blocks so
    /// a small world does not hold a 128 MiB block per memory type, 8 MiB of
    /// staging (a payload that does not fit takes a temporary buffer), and an
    /// arena that starts at a quarter of the default. The per-frame ring keeps
    /// the default size: it has no fallback when a frame does not fit, and one
    /// measured peak is not enough to shrink it.</summary>
    public static GpuMemoryProfile Compact { get; } = new(
        Name: "compact",
        BlockSizeBytes: 16 * MiB,
        DedicatedThresholdBytes: 8 * MiB,
        StagingCapacityBytes: 8 * MiB,
        RingCapacityBytesPerSlot: 16 * 1024 * 1024,
        MeshArenaInitialVertices: 256 * 1024,
        MeshArenaInitialIndices: 1024 * 1024);

    /// <summary>The profile a graphics preset runs with: Potato is compact, every other preset default.</summary>
    public static GpuMemoryProfile For(QualityPreset preset) =>
        preset == QualityPreset.Potato ? Compact : Default;
}
