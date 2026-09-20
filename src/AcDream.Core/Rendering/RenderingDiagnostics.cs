using System.Globalization;

namespace AcDream.Core.Rendering;

public static class RenderingDiagnostics
{
    public static int LightDebugMode { get; set; } =
        int.TryParse(
            Environment.GetEnvironmentVariable("ACDREAM_LIGHT_DEBUG"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int mode)
            ? mode
            : 0;

    /// <summary>Returns true for AC indoor EnvCell ids.</summary>
    public static bool IsEnvCellId(ulong id) => (id & 0xFFFFu) >= 0x0100u;

    public static bool ShouldRenderIndoor(uint playerCellId, bool renderRootResolved)
        => renderRootResolved && IsEnvCellId(playerCellId);

    /// <summary>Permanent frame-profiler toggle.</summary>
    public static bool FrameProfEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_FRAME_PROF") == "1";

    /// <summary>Optional per-frame CSV history path for the frame profiler.</summary>
    public static string? FrameHistoryPath { get; } =
        Environment.GetEnvironmentVariable("ACDREAM_FRAME_HISTORY");

    /// <summary>Per-stage GPU attribution: timestamp queries bracketing the
    /// shadow, static, dynamic, particle, post-process and UI work of a frame.
    /// Off by default; it costs two timestamp writes per stage and one query
    /// read-back per frame.</summary>
    public static bool GpuStageProfEnabled { get; set; } =
        Environment.GetEnvironmentVariable("ACDREAM_GPU_STAGE_PROF") == "1";

    public static bool DumpWalkTranscriptEnabled { get; set; }
}
