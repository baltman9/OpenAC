using System.Diagnostics;

namespace AcDream.App.UI;

/// <summary>
/// Calls one drawing callback per frame and takes it off the frame when it
/// stops behaving.
///
/// <para>A drawing callback runs inside the interface's own paint, between
/// someone else's push and that someone's matching pop. Two things there are
/// unrecoverable if nobody watches for them. One is time: the callback is on
/// the frame's critical path, so a slow one shows up directly as a lower
/// frame rate with no hint of where it went. The other is the drawing state:
/// the clip rectangle, the alpha and the origin are three stacks, and a
/// callback that pushes without popping shifts, dims or crops everything
/// drawn after it -- anywhere on the screen, with nothing pointing back at
/// the callback.</para>
///
/// <para>So each call is timed, and the three stack depths are read before
/// and compared after. A callback over budget for
/// <see cref="ConsecutiveOverrunsBeforeTrip"/> frames in a row is dropped; a
/// callback that throws or leaves the stacks unbalanced is dropped at once,
/// because either one means it has already stopped doing what it says. A
/// dropped callback is reported once and never called again.</para>
///
/// <para>The depth can be put back; the values cannot. A callback that popped
/// a level belonging to its caller destroyed the clip rectangle or origin
/// that level held, and no one outside knows what it was. What is pushed back
/// in its place is whatever is current after the over-popping: the caller's
/// OUTER clip, which is wider, or no clipping at all if the callback emptied
/// the stack. So the rest of that scope can draw outside the window it
/// belongs to, not merely inside a smaller piece of it -- the damage is
/// content in the wrong place rather than content missing. Restoring the
/// depth keeps the rest of the frame's pushes and pops paired, which is the
/// most that can be salvaged, and since such a callback is dropped on the
/// spot it is at most one frame of it.</para>
/// </summary>
internal sealed class UiDrawCallbackGuard
{
    /// <summary>
    /// How long one call may take before the frame counts it as an overrun.
    /// Two milliseconds is about an eighth of a 60-per-second frame: enough
    /// for real drawing work, far short of anything a player would feel.
    /// </summary>
    internal const double FrameBudgetMilliseconds = 2.0;

    /// <summary>
    /// How many overruns in a row it takes to drop the callback. More than
    /// one, so a single stall -- a texture upload, a garbage collection that
    /// happened to land inside the call -- does not cost a plugin its
    /// drawing; few enough that a genuinely slow one is gone in well under a
    /// tenth of a second.
    /// </summary>
    internal const int ConsecutiveOverrunsBeforeTrip = 3;

    private readonly string _name;
    private readonly string _callUnit;
    private readonly Action<string> _report;
    private readonly Func<double> _nowMilliseconds;
    private int _consecutiveOverruns;

    /// <param name="name">What the callback is called in the report that drops it.</param>
    /// <param name="report">Where the one report goes; the client's log by default.</param>
    /// <param name="nowMilliseconds">The clock, for tests; the stopwatch by default.</param>
    /// <param name="budgetMilliseconds">
    /// How long one call may take before it counts as an overrun.
    /// <see cref="FrameBudgetMilliseconds"/> by default; a callback that
    /// draws into its own off-screen surface once in a while rather than
    /// on every frame may be given more.
    /// </param>
    /// <param name="callUnit">
    /// What one call is called in the overrun report: "frames" for a
    /// callback run once a frame, "events" for one run per pointer event.
    /// </param>
    internal UiDrawCallbackGuard(
        string name,
        Action<string>? report = null,
        Func<double>? nowMilliseconds = null,
        double budgetMilliseconds = FrameBudgetMilliseconds,
        string callUnit = "frames")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetMilliseconds);
        ArgumentException.ThrowIfNullOrWhiteSpace(callUnit);
        _name = name;
        _callUnit = callUnit;
        BudgetMilliseconds = budgetMilliseconds;
        _report = report ?? (line => Serilog.Log.Warning("{Line}", line));
        // One clock for the whole guard: a budget measured against two
        // different time sources is a comparison between unrelated numbers.
        _nowMilliseconds = nowMilliseconds
            ?? (static () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>How long one call may take before it counts as an overrun.</summary>
    internal double BudgetMilliseconds { get; }

    /// <summary>True once the callback has been dropped for good.</summary>
    internal bool IsTripped { get; private set; }

    /// <summary>How long the last call took, in milliseconds.</summary>
    internal double LastMilliseconds { get; private set; }

    /// <summary>How many overruns have arrived back to back.</summary>
    internal int ConsecutiveOverruns => _consecutiveOverruns;

    /// <summary>
    /// Runs the callback unless it has been dropped. Returns true when it ran
    /// to the end and left the drawing state as it found it -- including on
    /// the overrun that drops it, because that call did draw.
    /// </summary>
    internal bool Invoke(UiRenderContext context, Action<UiRenderContext> draw)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draw);
        if (IsTripped) return false;

        int clipDepth = context.ClipStackDepth;
        int transformDepth = context.TransformStackDepth;
        int alphaDepth = context.AlphaStackDepth;
        double start = _nowMilliseconds();

        try
        {
            draw(context);
        }
        catch (Exception exception)
        {
            LastMilliseconds = _nowMilliseconds() - start;
            RestoreDepths(context, clipDepth, transformDepth, alphaDepth);
            Trip($"threw {exception.GetType().Name}: {exception.Message}");
            return false;
        }

        LastMilliseconds = _nowMilliseconds() - start;

        if (context.ClipStackDepth != clipDepth
            || context.TransformStackDepth != transformDepth
            || context.AlphaStackDepth != alphaDepth)
        {
            string left =
                $"clip {clipDepth}->{context.ClipStackDepth}, "
                + $"origin {transformDepth}->{context.TransformStackDepth}, "
                + $"alpha {alphaDepth}->{context.AlphaStackDepth}";
            RestoreDepths(context, clipDepth, transformDepth, alphaDepth);
            Trip($"left the drawing state unbalanced ({left})");
            return false;
        }

        if (LastMilliseconds <= BudgetMilliseconds)
        {
            _consecutiveOverruns = 0;
            return true;
        }

        _consecutiveOverruns++;
        if (_consecutiveOverruns >= ConsecutiveOverrunsBeforeTrip)
            TripForOverruns();

        // It did draw, and it drew correctly -- it was only slow.
        return true;
    }

    /// <summary>
    /// Runs a callback that draws nothing -- a pointer handler -- under the
    /// same time budget and the same exception rule, with no drawing state
    /// to check. Returns true when it ran to the end, including on the
    /// overrun that drops it.
    /// </summary>
    internal bool Invoke(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (IsTripped) return false;

        double start = _nowMilliseconds();
        try
        {
            callback();
        }
        catch (Exception exception)
        {
            LastMilliseconds = _nowMilliseconds() - start;
            Trip($"threw {exception.GetType().Name}: {exception.Message}");
            return false;
        }

        LastMilliseconds = _nowMilliseconds() - start;
        if (LastMilliseconds <= BudgetMilliseconds)
        {
            _consecutiveOverruns = 0;
            return true;
        }

        _consecutiveOverruns++;
        if (_consecutiveOverruns >= ConsecutiveOverrunsBeforeTrip)
            TripForOverruns();
        return true;
    }

    private void TripForOverruns() =>
        Trip(
            $"took more than {BudgetMilliseconds:0.##} ms on "
            + $"{_consecutiveOverruns} {_callUnit} in a row "
            + $"(last {LastMilliseconds:0.##} ms)");

    private static void RestoreDepths(
        UiRenderContext context, int clipDepth, int transformDepth, int alphaDepth)
    {
        while (context.ClipStackDepth > clipDepth) context.PopClip();
        while (context.ClipStackDepth < clipDepth) context.PushClipUnchanged();
        while (context.TransformStackDepth > transformDepth) context.PopTransform();
        while (context.TransformStackDepth < transformDepth) context.PushTransform(0f, 0f);
        while (context.AlphaStackDepth > alphaDepth) context.PopAlpha();
        while (context.AlphaStackDepth < alphaDepth) context.PushAlpha(1f);
    }

    private void Trip(string because)
    {
        IsTripped = true;
        _report($"UI draw callback '{_name}' dropped for the rest of the session: {because}.");
    }
}
