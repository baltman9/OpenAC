using AcDream.Core.Net;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>
/// One of the two clients, with no window and no graphics card, driven the
/// way its own host drives it. Each arm builds its host's REAL gameplay
/// operations and its host's REAL capability record over a runtime and a
/// world connection of the same shape, so what a scenario sees through
/// <see cref="Host"/> is that host's behaviour and not a stand-in for it.
/// </summary>
internal abstract class ParityArm : IDisposable
{
    /// <summary>The step both arms advance by, every time.</summary>
    internal const double TickSeconds = 0.015d;

    /// <summary>
    /// The words both arms are configured to be found by. Real clients read
    /// these from their own configuration -- a launch option on one, the
    /// session file on the other -- so the arms stand in for that with one
    /// list, and a client that cannot carry them shows up as a difference.
    /// </summary>
    internal static readonly string[] ConfiguredPluginTags =
        ["parity-tag", "parity-second"];

    /// <summary>The plugin ids both arms are given startup settings for.</summary>
    internal const string SettingsPluginId = "acdream.parity.settings";

    /// <summary>A second one, so a plugin cannot read another plugin's settings.</summary>
    internal const string OtherSettingsPluginId = "acdream.parity.other";

    /// <summary>
    /// The startup settings both arms are configured with. Real clients read
    /// these from their own configuration -- a launch option naming a file on
    /// one, the session file on the other -- and both end at this one shape,
    /// so the arms stand in for that with one map.
    /// </summary>
    internal static readonly AcDream.Runtime.Plugins.PluginSessionSettings
        ConfiguredPluginSettings =
        AcDream.Runtime.Plugins.PluginSessionSettings.FromDeclared(
            new Dictionary<string, Dictionary<string, string>>
            {
                [SettingsPluginId] = new()
                {
                    ["startMacro"] = "true",
                    ["profile"] = "parity",
                },
                [OtherSettingsPluginId] = new()
                {
                    ["startMacro"] = "false",
                },
            });

    private readonly IDisposable _hostLease;
    private ParityPlayerBody? _body;
    private RuntimeLocalPlayerFrameController? _frame;
    private bool _disposed;

    protected ParityArm(
        string name,
        Func<ParitySessionOperations, GameRuntimeDependencies> buildDependencies)
    {
        Name = name;
        // A real client announces itself to the other clients on this machine
        // by writing into the player's own data folder. Each arm gets a
        // scratch folder of its own instead, so a scenario can read what this
        // client announced without touching anything the player owns and
        // without the two arms seeing each other.
        DataDirectory = Path.Combine(
            Path.GetTempPath(),
            "acdream-parity",
            $"{name}-{Guid.NewGuid():N}");
        Operations = new ParitySessionOperations();
        Dependencies = buildDependencies(Operations);
        Runtime = new GameRuntime(Dependencies);
        _hostLease = Runtime.AcquireHostLease($"{name} parity arm");
        // This client's OWN session bindings, from its own builder. They used
        // to be one record written here for both arms, which meant the thing
        // each client really hands its session host -- the record that decides
        // what happens on connect, on taking hold of a character and on
        // arriving in the world -- was the one thing in the harness no
        // scenario ran. It is built here rather than after the constructor
        // because the session host takes it at birth, so an arm's builder may
        // only read what is already standing: the runtime, this arm's scratch
        // folder, and its own field initialisers.
        Session = new LiveSessionHost(
            Runtime.Session,
            CreateSessionHostBindings(
                new LiveSessionRoutingFactories(
                    CreateEventRoute,
                    CreateCommandRoute),
                generation =>
                    Runtime.ResetGeneration(generation, InertReset.Instance)),
            new LiveSessionConnectOptions(
                true, "127.0.0.1", 9000, "parity", "parity"),
            runtime: Runtime);
    }

    /// <summary>Which client this is, for failure messages.</summary>
    internal string Name { get; }

    /// <summary>This arm's stand-in for the player's own data folder.</summary>
    internal string DataDirectory { get; }

    /// <summary>
    /// The folder this client's announcements land in, read by a scenario the
    /// way a second client on the same machine reads them.
    /// </summary>
    internal string PeerDirectory => Path.Combine(DataDirectory, "plugin-peers");

    internal GameRuntimeDependencies Dependencies { get; }

    internal GameRuntime Runtime { get; }

    internal LiveSessionHost Session { get; }

    internal ParitySessionOperations Operations { get; }

    /// <summary>What a plugin sees. Built by the host, not by the harness.</summary>
    internal abstract IPluginHost Host { get; }

    /// <summary>
    /// Where a line typed into the chat box goes on this client: the host's
    /// own outbound command route, which is what turns a submitted line into
    /// speech, a tell, a channel message, a client command or nothing.
    /// </summary>
    internal abstract AcDream.Runtime.Chat.IPluginCommandBus Commands { get; }

    /// <summary>What the binding pass said about seams this arm left empty.</summary>
    internal abstract IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// The bindings this client really hands its session host, from this
    /// client's own builder, with the parts it can supply here. A part that
    /// only a drawn world or a console can provide is stood in for and said
    /// so; nothing is written here that the client does not write itself.
    /// </summary>
    /// <param name="routing">How this arm takes hold of a world connection.</param>
    /// <param name="reset">Puts every owner back for a new generation.</param>
    protected abstract LiveSessionHostBindings CreateSessionHostBindings(
        LiveSessionRoutingFactories routing,
        Action<RuntimeGenerationToken> reset);

    /// <summary>
    /// The bindings this client really hands its inbound event route for the
    /// character it is playing: the skill formulas, the confirmation hooks,
    /// and what happens when the server changes how fast the character runs.
    /// </summary>
    internal abstract LiveCharacterSessionBindings
        CreateCharacterSessionBindings();

    /// <summary>
    /// The client is asking the player to accept or decline something, raised
    /// from the same place this client raises it for real. Nothing here draws
    /// a dialog; what a scenario is after is whether a plugin is told.
    /// </summary>
    /// <param name="confirmation">What is being asked, and under which id.</param>
    internal abstract void ShowConfirmation(
        AcDream.Plugin.Abstractions.PluginConfirmation confirmation);

    /// <summary>Opens the session and walks it into the world.</summary>
    internal void EnterWorld()
    {
        _ = Session.Start(Runtime.Generation);
        // The character is in the world from here, as it is in a real
        // session once the server has said so: everything a client checks
        // that against -- every use, pickup and description it sends -- is
        // refused outright until then, on both clients alike.
        Server.LetTheCharacterIn();
        Runtime.SyncLifecycleEmission();
        // Each client says its own lines about the session opening, and says
        // them in its own place: the one with a window writes them into the
        // chat log the player reads, the one without writes them to its
        // console. That is a real difference and has a test of its own
        // (see the session-opening lines test on the chat route); every other
        // scenario is about what happens once the character is in, so the
        // log starts empty here rather than carrying one client's opening
        // lines and not the other's.
        Runtime.CommunicationOwner.Chat.Clear();
    }

    /// <summary>
    /// Takes ownership of the character's body, and stands up this host's own
    /// per-frame movement driver over it, so from here on the arm advances a
    /// character the way its client does.
    /// </summary>
    internal ParityPlayerBody AdoptPlayerBody(ParityPlayerBody body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (_body is not null)
            throw new InvalidOperationException(
                "This arm already has a character.");
        _body = body;
        _frame = CreateFrameController();
        return body;
    }

    /// <summary>
    /// Whether the character has a body that can be moved. A scenario about
    /// moving or swinging asserts this first: without it both arms answer
    /// "no body" and agreeing proves nothing.
    /// </summary>
    internal bool HasLiveBody => _body?.HasLiveBody == true;

    /// <summary>
    /// One step of the shared clock, the same length on both arms, in the
    /// order a client takes it: the body advances and says what it did on the
    /// object side of the inbound-network barrier, the session runs, then the
    /// post-network command phase closes the frame.
    /// </summary>
    internal void Advance() => Advance(TickSeconds);

    /// <summary>
    /// The character's own step and the close after it, and nothing else: no
    /// network, no post-network commands, no plugin tick. Staging uses it to
    /// let the body finish the pose it took on arrival without the client
    /// telling anyone anything in the meantime.
    /// </summary>
    internal void StepBody()
    {
        _ = Runtime.AdvanceFrameClock(TickSeconds);
        _body?.Drive();
        _frame?.AdvanceBeforeNetwork((float)TickSeconds);
        Runtime.FinishRemoteBodyPass();
    }

    /// <summary>
    /// The same step, over however long this arm's client says its frame or
    /// turn really took. A client with a window draws at whatever rate it
    /// manages and a client without one takes a turn on a schedule, so an
    /// arm standing in for either has to be able to say so.
    /// </summary>
    internal virtual void Advance(double hostDeltaSeconds)
    {
        // The frame step both clients take, rule and all.
        _ = Runtime.AdvanceFrameClock(hostDeltaSeconds);
        _body?.Drive();
        _frame?.AdvanceBeforeNetwork((float)hostDeltaSeconds);
        // The close both clients run after the character's own step and
        // before anything the server said this frame is read: it is where a
        // combat-mode change parked behind a motion learns the motion is over.
        Runtime.FinishRemoteBodyPass();
        Runtime.Session.Tick();
        _frame?.RunPostNetworkCommandPhase();
        OnAdvanced(hostDeltaSeconds);
    }

    /// <summary>
    /// This host's own local-player frame driver, over this host's own frame
    /// host. Both are the classes the real client builds.
    /// </summary>
    protected abstract RuntimeLocalPlayerFrameController CreateFrameController();

    /// <summary>Whatever this host does once per step beyond ticking.</summary>
    /// <param name="hostDeltaSeconds">How long this frame or turn took.</param>
    protected virtual void OnAdvanced(double hostDeltaSeconds)
    {
    }

    /// <summary>
    /// The server, for this arm. Available once the session has opened; a
    /// scenario scripts what the server said through it and the arm's own
    /// inbound route carries it the rest of the way.
    /// </summary>
    internal ParityServer Server => _server
        ?? throw new InvalidOperationException(
            "This arm has no world connection yet.");

    private ParityServer? _server;

    /// <summary>
    /// The route both clients build: the production live-session event router
    /// over the runtime's own entity controller, attached to this arm's world
    /// connection. See <see cref="ParityInboundRoute"/> for the three host
    /// hooks that cannot be built windowless.
    /// </summary>
    private ILiveSessionEventRouting CreateEventRoute(WorldSession session)
    {
        _server = new ParityServer(
            session,
            () => Runtime.PlayerIdentity.ServerGuid);
        return ParityInboundRoute.Create(
            Runtime,
            session,
            CreateCharacterSessionBindings());
    }

    /// <summary>Delivers something the server would have said.</summary>
    internal void Deliver(Action<GameRuntime> delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        delivery(Runtime);
    }

    /// <summary>How this host takes hold of the world connection.</summary>
    protected abstract ILiveSessionCommandRouting CreateCommandRoute(
        WorldSession session);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Runtime.Session.CurrentSession is not null)
            _ = Session.Stop(Runtime.Generation);
        _body?.Dispose();
        DisposeHost();
        _hostLease.Dispose();
        Runtime.Dispose();
        try
        {
            if (Directory.Exists(DataDirectory))
                Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    protected virtual void DisposeHost()
    {
    }

    private sealed class InertReset : IRuntimeGenerationResetHost
    {
        internal static readonly InertReset Instance = new();

        public void RetireEntityProjection(
            AcDream.Runtime.Entities.RuntimeEntityRecord entity)
        {
        }

        public void DrainEntityProjectionBoundary()
        {
        }

        public void CompleteEntityProjectionRetirement()
        {
        }
    }
}

/// <summary>A logger that keeps what it was told and says nothing.</summary>
internal sealed class RecordingPluginLogger : IPluginLogger
{
    private readonly List<string> _lines = [];

    internal IReadOnlyList<string> Lines => _lines;

    public void Info(string message) => _lines.Add($"info: {message}");

    public void Warn(string message) => _lines.Add($"warn: {message}");

    public void Error(string message, Exception? exception = null) =>
        _lines.Add($"error: {message}");
}
