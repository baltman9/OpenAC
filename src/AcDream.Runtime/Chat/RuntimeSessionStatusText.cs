using System.Globalization;

namespace AcDream.Runtime.Chat;

/// <summary>
/// What <c>/status</c> answers: the session in one line. It is built here so
/// a console and a chat box cannot drift into printing two different lines
/// for the same question.
/// </summary>
public static class RuntimeSessionStatusText
{
    /// <summary>The verb this text answers, on every client.</summary>
    public const string Verb = "status";

    /// <summary>
    /// The session's generation, where it is in its life, and where the
    /// character stands. US-formatted whatever the machine's locale is, so
    /// two clients print the same numbers.
    /// </summary>
    public static string For(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        RuntimeMovementSnapshot movement =
            runtime.MovementOwner.Snapshot;
        string position = movement.HasController
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"cell=0x{movement.Position.ObjCellId:X8} "
                + $"local=({movement.Position.Frame.Origin.X:F2},"
                + $"{movement.Position.Frame.Origin.Y:F2},"
                + $"{movement.Position.Frame.Origin.Z:F2})")
            : "unknown";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"generation={runtime.Generation.Value} "
            + $"state={runtime.Lifecycle.State} "
            + $"position={position}");
    }
}
