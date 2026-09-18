namespace AcDream.App.Rendering;

/// <summary>
/// Growth policy for the large per-frame scratch arrays. Doubling on demand
/// and never shrinking leaves every array at up to twice the densest area a
/// session has visited, and each doubling retires a large array into the
/// large-object heap. Growth here adds a quarter of slack, and an array that
/// is about to be refilled shrinks once fewer than a third of it is needed,
/// which leaves a gap wide enough that a steady workload never resizes.
/// </summary>
internal static class ScratchArrays
{
    /// <summary>Elements below which shrinking is never worth a reallocation.</summary>
    internal const int ShrinkFloor = 4096;

    /// <summary>Ensures room for <paramref name="required"/> elements that the
    /// caller is about to write from index zero, growing or shrinking.</summary>
    internal static void EnsureRefillCapacity<T>(ref T[] values, int required, int minimum = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(required);
        int length = values.Length;
        if (length < required)
        {
            values = new T[GrownCapacity(length, required, minimum)];
            return;
        }

        if (length > ShrinkFloor && (long)required * 3 < length)
            values = new T[Math.Max(GrownCapacity(0, required, minimum), ShrinkFloor)];
    }

    /// <summary>Ensures room for <paramref name="required"/> elements while the
    /// caller keeps the existing contents; never shrinks.</summary>
    internal static void EnsureAppendCapacity<T>(ref T[] values, int required, int minimum = 16)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(required);
        if (values.Length >= required)
            return;
        Array.Resize(ref values, GrownCapacity(values.Length, required, minimum));
    }

    private static int GrownCapacity(int length, int required, int minimum)
    {
        long slack = checked((long)required + required / 4);
        long fromLength = checked((long)length + length / 4);
        long capacity = Math.Max(Math.Max(slack, fromLength), minimum);
        return capacity > int.MaxValue ? int.MaxValue : (int)capacity;
    }
}

/// <summary>
/// Hands a frame-scoped scratch array its capacity back after a peak. One
/// pass over a dense scene can leave an append-only buffer many times larger
/// than a normal frame needs, and it would hold that for the rest of the
/// session.
///
/// <para><see cref="Observe"/> must be called at a frame boundary, where
/// nothing points into the buffer: it can replace the array, and anything
/// written in an earlier frame is gone.</para>
/// </summary>
internal struct FrameScratchTrim(int everyFrames, int floor)
{
    private int _framesSinceTrim;
    private int _peakSinceTrim;

    /// <summary>Elements used in the frame just finished, highest since the
    /// last trim.</summary>
    internal readonly int PeakSinceTrim => _peakSinceTrim;

    /// <summary>Frames observed since the last trim.</summary>
    internal readonly int FramesSinceTrim => _framesSinceTrim;

    /// <summary>Records the frame's usage and, every <c>everyFrames</c>
    /// frames, gives back anything beyond twice the peak seen since the last
    /// trim. Never shrinks below <c>floor</c>.</summary>
    internal void Observe<T>(ref T[] buffer, int usedThisFrame)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(usedThisFrame);
        if (usedThisFrame > _peakSinceTrim)
            _peakSinceTrim = usedThisFrame;
        if (++_framesSinceTrim < everyFrames)
            return;

        int target = Math.Max(_peakSinceTrim, floor);
        if (buffer.Length > (long)target * 2)
            buffer = new T[target];
        _framesSinceTrim = 0;
        _peakSinceTrim = 0;
    }
}
