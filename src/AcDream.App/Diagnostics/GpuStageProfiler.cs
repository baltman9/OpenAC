using System.Globalization;
using System.Text;
using AcDream.App.Rendering.Gpu;
using AcDream.Core.Rendering;

namespace AcDream.App.Diagnostics;

/// <summary>
/// Per-stage GPU attribution. Renderers bracket their own work with
/// <see cref="Measure"/>; this owner reads the resolved ranges once per frame
/// and reports a rolling median per stage on the frame profiler's cadence.
/// <para>The ranges are sequential — one starts only after every earlier
/// command has completed — so the stages of a frame add up to that frame's GPU
/// time instead of each one counting the wait for everything before it.</para>
/// <para><b>Bracket a whole render pass, not part of one.</b> Measured on a
/// dense outdoor scene, ranges nested inside one pass reported about a seventh
/// of what the drawing they bracket actually costs: a comparison against a run
/// with that drawing switched off put one of them at 1.7 ms where its range
/// read 0.16 ms. A range that spans a pass reads correctly. To attribute work
/// inside a pass, compare runs with parts of it switched off.</para>
/// <para>Off unless <see cref="RenderingDiagnostics.GpuStageProfEnabled"/> is
/// set.</para>
/// </summary>
public sealed class GpuStageProfiler
{
    private const int WindowCapacity = 2048;
    private const long ReportIntervalTicks = 5 * TimeSpan.TicksPerSecond;

    /// <summary>The instance renderers reach from inside a pass. A stage range
    /// has to be recorded where the draws are, and those call sites sit several
    /// layers below anything that could be handed a profiler.</summary>
    public static GpuStageProfiler Instance { get; } = new();

    private const char OccurrenceMarker = '#';

    private const string PassStagePrefix = "post/";

    private static readonly Dictionary<(string Name, int Occurrence), string> KeyCache = new();
    private static readonly Dictionary<string, string> PassStageNames = new(StringComparer.Ordinal);

    private readonly List<string> _order = [];
    private readonly Dictionary<string, FrameStatsBuffer> _stages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameStatsBuffer> _stageRanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rangesThisSample = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _snapshot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _usedThisFrame = new(StringComparer.Ordinal);
    private int _lastGeneration = -1;
    private int _droppedRanges;
    private long _lastReportTicks;
    private int _samplesInWindow;

    /// <summary>The last reported line, for tests and for a live overlay.</summary>
    public string? LastReport { get; private set; }

    public static bool Enabled => RenderingDiagnostics.GpuStageProfEnabled;

    /// <summary>Brackets one stage of GPU work. Null when the probe is off, so
    /// the call site pays a branch.</summary>
    internal static IDisposable? Measure(IGpuPassEncoder? encoder, string stageName)
    {
        if (!Enabled || encoder is null)
            return null;
        return encoder.BeginStageTimerScope(Instance.NextKey(stageName));
    }

    /// <summary>A stage that recurs within one frame gets its own key, so the
    /// second and later occurrences are reported instead of colliding with the
    /// first (the backend refuses two ranges under one name).</summary>
    internal string NextKey(string stageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        int occurrence = _usedThisFrame.GetValueOrDefault(stageName) + 1;
        _usedThisFrame[stageName] = occurrence;
        if (occurrence == 1)
            return stageName;
        if (!KeyCache.TryGetValue((stageName, occurrence), out string? key))
        {
            key = stageName + OccurrenceMarker + occurrence.ToString(CultureInfo.InvariantCulture);
            KeyCache[(stageName, occurrence)] = key;
        }

        return key;
    }

    internal void BeginFrame() => _usedThisFrame.Clear();

    /// <summary>The stage key for a pass that already measures itself under
    /// <paramref name="passName"/>. Both ranges cover the same pass — the pass
    /// timer from the top of the pipeline, which is what a render pack budgets
    /// against, and the stage range sequentially — and the backend refuses two
    /// ranges under one name, so the keys have to differ. Every pass-level call
    /// site derives its key here rather than spelling a prefix of its own.
    /// </summary>
    internal static string PassStageName(string passName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passName);
        if (PassStageNames.TryGetValue(passName, out string? stage))
            return stage;
        stage = PassStagePrefix + passName;
        PassStageNames[passName] = stage;
        return stage;
    }

    /// <summary>A stage recorded several times in one frame reports as one
    /// total, so "terrain" is the frame's terrain time and not its last batch.</summary>
    internal static string BaseStageName(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        int marker = key.IndexOf(OccurrenceMarker);
        return marker < 0 ? key : key[..marker];
    }

    /// <summary>Reads whatever the backend resolved since the last call.</summary>
    internal void Collect(IGpuTimerPool timers)
    {
        ArgumentNullException.ThrowIfNull(timers);
        if (!Enabled || !timers.IsSupported)
            return;
        if (timers.ResolveGeneration == _lastGeneration)
            return;
        _lastGeneration = timers.ResolveGeneration;

        _snapshot.Clear();
        NoteDroppedRanges(timers.DroppedScopes);
        IReadOnlyList<(string Name, double Milliseconds)> resolved = timers.LastResolved;
        for (int i = 0; i < resolved.Count; i++)
        {
            (string name, double milliseconds) = resolved[i];
            string stage = BaseStageName(name);
            _snapshot[stage] = _snapshot.GetValueOrDefault(stage) + milliseconds;
            _rangesThisSample[stage] = _rangesThisSample.GetValueOrDefault(stage) + 1;
        }

        foreach ((string stage, double milliseconds) in _snapshot)
            Record(stage, milliseconds, _rangesThisSample.GetValueOrDefault(stage));
        _rangesThisSample.Clear();
        _samplesInWindow++;

        long nowTicks = DateTime.UtcNow.Ticks;
        if (_lastReportTicks == 0)
        {
            _lastReportTicks = nowTicks;
            return;
        }
        if (nowTicks - _lastReportTicks < ReportIntervalTicks)
            return;

        LastReport = FormatReport();
        Console.WriteLine(LastReport);
        _lastReportTicks = nowTicks;
        ResetWindow();
    }

    /// <summary>Starts a fresh reporting window.</summary>
    internal void ResetWindow()
    {
        _samplesInWindow = 0;
        foreach (FrameStatsBuffer buffer in _stages.Values)
            buffer.Reset();
        foreach (FrameStatsBuffer buffer in _stageRanges.Values)
            buffer.Reset();
    }

    /// <summary>How many ranges the backend refused this session because the
    /// per-frame budget was full.</summary>
    internal void NoteDroppedRanges(int dropped) => _droppedRanges = dropped;

    internal void Record(string stage, double milliseconds, int rangeCount = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        if (!_stages.TryGetValue(stage, out FrameStatsBuffer? buffer))
        {
            buffer = new FrameStatsBuffer(WindowCapacity);
            _stages.Add(stage, buffer);
            _stageRanges.Add(stage, new FrameStatsBuffer(WindowCapacity));
            _order.Add(stage);
        }

        buffer.Push((long)Math.Round(milliseconds * 1000d));
        _stageRanges[stage].Push(rangeCount);
    }

    internal string FormatReport()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(256);
        sb.Append("[gpu-stage] n=").Append(_samplesInWindow);
        if (_droppedRanges > 0)
        {
            // The budget filled up, so some stages are missing from the line
            // rather than reported as costing nothing.
            sb.Append(" | DROPPED=").Append(_droppedRanges);
        }
        foreach (string stage in _order)
        {
            FrameStatsBuffer buffer = _stages[stage];
            if (buffer.Count == 0)
                continue;
            sb.AppendFormat(
                ci,
                " | {0} p50={1:0.000} p95={2:0.000} x{3}",
                stage,
                buffer.Percentile(0.50) / 1000d,
                buffer.Percentile(0.95) / 1000d,
                _stageRanges[stage].Percentile(0.50));
        }

        return sb.ToString();
    }
}
