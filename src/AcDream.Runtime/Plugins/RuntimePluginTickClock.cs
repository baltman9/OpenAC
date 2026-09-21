namespace AcDream.Runtime.Plugins;

/// <summary>
/// The one clock the plugin tick is raised from, for whichever client is
/// running.
/// <para>
/// A client with a window updates as fast as it draws and a client without
/// one takes a turn on a fixed schedule, so a plugin ticked straight off the
/// host's frame got a different number of ticks per second, each carrying a
/// different slice of elapsed time, depending on which client it was loaded
/// into. Anything a plugin paces off that tick -- a timer it counts down, a
/// rule it re-evaluates, a total it accumulates -- then behaved differently
/// for the same plugin on the same character.
/// </para>
/// <para>
/// Each client feeds this clock however long its own frame or turn really
/// took. The clock holds the time until a whole step has gone by, then
/// raises the plugin tick with exactly one step of elapsed time, as many
/// times over as the time in hand allows, and carries the remainder into the
/// next feed. Nothing about the client's own frame changes: what it draws,
/// reads and simulates still runs once per frame at whatever rate it runs
/// at.
/// </para>
/// <para>
/// The plugin tick keeps its cadence while there is no world to simulate --
/// arriving at login, or between giving up one world and standing up the
/// next through a portal -- exactly as the shared frame clock keeps counting
/// frames while it holds the world's own clock still. A plugin waiting for
/// the world to come back has to keep being asked in order to notice that it
/// has.
/// </para>
/// </summary>
public sealed class RuntimePluginTickClock
{
    /// <summary>
    /// How much time one plugin tick carries, in seconds. Every tick reports
    /// exactly this, whatever the client's own frame rate is.
    /// </summary>
    public const double StepSeconds = 0.015;

    /// <summary>
    /// The most ticks one feed may raise before the rest of the time held is
    /// dropped.
    /// <para>
    /// A client that stalls -- a long load, a machine put to sleep, a
    /// debugger -- comes back with a large slice of time in hand. Spending
    /// all of it would hand plugins a burst of hundreds of ticks inside one
    /// frame, which both costs more than the frame that follows it and is a
    /// worse answer than losing the stall: the world moved on without them,
    /// and replaying the gap tick by tick does not bring it back. The bound
    /// is the number of whole steps in the longest single stretch of elapsed
    /// time the client's existing fixed-step clock ever hands to anything in
    /// one go (0.2 s), so a stall costs at most that much catch-up work.
    /// </para>
    /// </summary>
    public const int MaxStepsPerFeed = 13;

    private readonly Action<double> _raiseTick;
    private readonly Func<ulong>? _generation;
    private ulong _lastGeneration;
    private bool _hasGeneration;
    private double _heldSeconds;

    /// <summary>
    /// Builds the clock over the call that raises the plugin tick.
    /// </summary>
    /// <param name="raiseTick">
    /// Raises the client's plugin tick, carrying the elapsed seconds it is
    /// given.
    /// </param>
    /// <param name="generation">
    /// Reports which run of the session the client is on, so time held from
    /// a run that has ended is not spent on the one that replaced it. Null
    /// where there is no session to ask.
    /// </param>
    public RuntimePluginTickClock(
        Action<double> raiseTick,
        Func<ulong>? generation = null)
    {
        _raiseTick = raiseTick ?? throw new ArgumentNullException(nameof(raiseTick));
        _generation = generation;
    }

    /// <summary>
    /// The time taken in but not yet spent on a tick, in seconds. Always
    /// less than one step.
    /// </summary>
    public double HeldSeconds => _heldSeconds;

    /// <summary>
    /// Takes in however long the client's own frame or turn really took and
    /// raises the plugin tick once for every whole step that has gone by.
    /// </summary>
    /// <param name="hostDeltaSeconds">
    /// The real elapsed time of this frame or turn. Anything that is not a
    /// finite, positive number of seconds counts as no time at all.
    /// </param>
    /// <returns>How many ticks this feed raised.</returns>
    public int Feed(double hostDeltaSeconds)
    {
        if (_generation is not null)
        {
            ulong generation = _generation();
            if (!_hasGeneration || generation != _lastGeneration)
            {
                _hasGeneration = true;
                _lastGeneration = generation;
                _heldSeconds = 0.0;
            }
        }

        if (double.IsFinite(hostDeltaSeconds) && hostDeltaSeconds > 0.0)
            _heldSeconds += hostDeltaSeconds;

        int due = (int)Math.Min(
            _heldSeconds / StepSeconds,
            MaxStepsPerFeed + 1);
        if (due > MaxStepsPerFeed)
        {
            // Past the bound the client has fallen so far behind that the
            // time in hand says nothing useful any more; drop all of it
            // rather than carry a debt the next feed inherits.
            due = MaxStepsPerFeed;
            _heldSeconds = 0.0;
        }
        else
        {
            _heldSeconds -= due * StepSeconds;
        }

        for (int step = 0; step < due; step++)
            _raiseTick(StepSeconds);

        return due;
    }

    /// <summary>
    /// Drops the time held without raising anything, for a client starting a
    /// fresh run of the session.
    /// </summary>
    public void Reset() => _heldSeconds = 0.0;
}
