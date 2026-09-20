using AcDream.Core.Net;
using AcDream.Headless.Hosting;
using AcDream.Runtime.Chat;
using AcDream.Headless.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
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

    /// <summary>
    /// Where a typed line goes on this client, exactly as its session host
    /// hangs it: the shared chat command route behind the surface the host
    /// hands its console and its plugins.
    /// </summary>
    private readonly LiveChatCommandSurface _chatCommands = new();
    private DirectGameRuntimeCommandAdapter _commands = null!;

    internal WindowlessArm()
        : this(new HeadlessGameplayOperations())
    {
    }

    private WindowlessArm(HeadlessGameplayOperations gameplay)
        : base(
            ParityHost.Windowless,
            operations =>
            HeadlessAutomationCapabilities.BuildRuntimeDependencies(
                gameplay,
                TimeProvider.System,
                static _ => { },
                sessionOperations: operations))
    {
        _gameplay = gameplay;
        _gameplay.Bind(Runtime, catalog: null, accountName: () => "parity");
        // The windowless client's own command adapter, the one its session
        // host really builds, over this arm's session.
        _commands = new DirectGameRuntimeCommandAdapter(
            Runtime,
            new ParitySessionCommands(Session));
        _host = new HeadlessPluginHost(
            Runtime,
            _log,
            sessionCommands: _commands,
            dataDirectory: DataDirectory,
            pluginTags: ConfiguredPluginTags);
    }

    internal override IPluginHost Host => _host;

    internal override AcDream.Runtime.Chat.IPluginCommandBus Commands =>
        _chatCommands;

    internal override IReadOnlyList<string> Warnings => _log.Lines;

    /// <summary>Where this client's session host raises it.</summary>
    internal override void ShowConfirmation(PluginConfirmation confirmation) =>
        _host.RaiseConfirmationRequested(confirmation);

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

    /// <summary>
    /// This client takes hold of a world connection twice over: once for its
    /// gameplay operations and once for its command adapter, which answers
    /// nothing until it has been handed the connection.
    /// </summary>
    protected override ILiveSessionCommandRouting CreateCommandRoute(
        WorldSession session) =>
        new EveryRoute(
            _gameplay.CreateRoute(session),
            _commands.CreateRoute(session),
            // The third is the chat command route, which this client's
            // session host hangs beside the other two: without it a typed
            // line reaches nothing and every chat scenario compares silence.
            _chatCommands.Attach(new LiveChatCommandRoute(
                RuntimeChatCommandBindings.Create(Runtime, session))));

    private sealed class EveryRoute(
        params ILiveSessionCommandRouting[] routes)
        : ILiveSessionCommandRouting
    {
        public void Activate()
        {
            foreach (ILiveSessionCommandRouting route in routes)
                route.Activate();
        }

        public void Dispose()
        {
            for (int index = routes.Length - 1; index >= 0; index--)
                routes[index].Dispose();
        }
    }

    /// <summary>
    /// The windowless client's own frame host, unchanged: it reads the
    /// character's body and the world's facts about it off the runtime, and
    /// sends position through the shared outbound owner.
    /// </summary>
    protected override RuntimeLocalPlayerFrameController
        CreateFrameController() =>
        Runtime.CreateLocalPlayerFrameController(
            new HeadlessLocalPlayerFrameHost(Runtime, Session),
            new HeadlessMovementInputSource(Runtime.MovementOwner));

    protected override void DisposeHost() => _host.Dispose();

}
