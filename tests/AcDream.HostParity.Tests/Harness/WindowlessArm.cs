using AcDream.Core.Net;
using AcDream.Headless.Hosting;
using AcDream.Headless.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The windowless client, built the way a bot session builds it: the real
/// gameplay operations bound to the runtime, the real plugin host, and the
/// same shared binding pass over the record the host really makes.
///
/// What is NOT under this arm, and why:
/// * installed content, so the spell catalog, the skill names and the walk
///   controller are absent, which is what a content-less bot run looks like;
/// * the item automation and the logout automation, whose wiring belongs to
///   the session host rather than to the plugin host;
/// * the session's own scheduler, whose catch-up behaviour is a difference in
///   its own right and not one a scenario should smuggle in: the arm steps at
///   the same fixed rate as the other.
/// </summary>
internal sealed class WindowlessArm : ParityArm
{
    private readonly HeadlessGameplayOperations _gameplay;
    private readonly HeadlessPluginHost _host;
    private readonly RecordingPluginLogger _log = new();

    internal WindowlessArm()
        : this(new HeadlessGameplayOperations(), new ClockHolder())
    {
    }

    private WindowlessArm(HeadlessGameplayOperations gameplay, ClockHolder clock)
        : base(
            ParityHost.Windowless,
            operations =>
            HeadlessAutomationCapabilities.BuildRuntimeDependencies(
                gameplay,
                TimeProvider.System,
                static _ => { },
                sessionOperations: operations,
                combatTime: () =>
                    clock.Runtime?.Clock.SimulationTimeSeconds ?? 0d))
    {
        clock.Runtime = Runtime;
        _gameplay = gameplay;
        _gameplay.Bind(Runtime, catalog: null, accountName: () => "parity");
        _host = new HeadlessPluginHost(Runtime, _log);
    }

    internal override IPluginHost Host => _host;

    internal override IReadOnlyList<string> Warnings => _log.Lines;

    /// <summary>
    /// The windowless host drives the surface's bookkeeping and its plugin
    /// ticks from the session tick.
    /// </summary>
    protected override void OnAdvanced()
    {
        // The windowless session host ticks the attack owner and the plugin
        // host once per session tick.
        Runtime.ActionOwner.CombatAttack.Tick();
        _host.FireTick(TickSeconds);
    }

    protected override ILiveSessionCommandRouting CreateCommandRoute(
        WorldSession session) => _gameplay.CreateRoute(session);

    protected override void DisposeHost() => _host.Dispose();

    /// <summary>
    /// The combat clock reads the runtime that is about to be built, the same
    /// way the real session host does.
    /// </summary>
    private sealed class ClockHolder
    {
        internal GameRuntime? Runtime { get; set; }
    }
}
