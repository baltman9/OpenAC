using System.Globalization;
using System.Numerics;

namespace AcDream.App.Rendering.Walk;

internal static class BuildingDrawTrace
{
    internal static readonly uint CellId = ReadCell();
    private static long _nextTick;
    internal static bool Active { get; private set; }

    private static uint ReadCell()
    {
        string? value = Environment.GetEnvironmentVariable("ACDREAM_TRACE_BUILDING_CELL");
        if (value?.StartsWith("0x", StringComparison.OrdinalIgnoreCase) == true)
            value = value[2..];
        return uint.TryParse(value, NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out uint cell) ? cell : 0;
    }

    internal static void Begin(uint cameraCell, Vector3 camera)
    {
        Active = CellId != 0 && Environment.TickCount64 >= _nextTick;
        if (!Active) return;
        _nextTick = Environment.TickCount64 + 1000;
        Write($"cameraCell=0x{cameraCell:X8} camera={camera}");
    }

    internal static bool Matches(uint cell) => Active && cell == CellId;
    internal static void Write(string message)
    {
        if (Active) Console.WriteLine($"[building-draw] {message}");
    }
}
