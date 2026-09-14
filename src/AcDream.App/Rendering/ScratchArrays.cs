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
