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

    private readonly IDisposable _hostLease;
    private ParityPlayerBody? _body;
    private RuntimeLocalPlayerFrameController? _frame;
    private bool _disposed;

    protected ParityArm(
        string name,
        Func<ParitySessionOperations, GameRuntimeDependencies> buildDependencies)
    {
        Name = name;
        Operations = new ParitySessionOperations();
        Dependencies = buildDependencies(Operations);
        Runtime = new GameRuntime(Dependencies);
        _hostLease = Runtime.AcquireHostLease($"{name} parity arm");
        Session = new LiveSessionHost(
            Runtime.Session,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new InertEventRoute(),
                    CreateCommandRoute),
                generation => Runtime.ResetGeneration(generation, InertReset.Instance),
                new LiveSessionSelectionBindings(
                    id => Runtime.PlayerIdentity.ServerGuid = id,
                    _ => { },
                    Runtime.CommunicationOwner.Chat.SetLocalPlayerGuid,
                    _ => { },
                    _ => { },
                    Runtime.ActionOwner.Combat.Clear),
                new LiveSessionEnteredWorldBindings(
                    _ => { },
                    () => { },
                    () => { },
                    _ => { },
                    () => { }),
                (_, _, _) => { },
                () => { },
                _ => { },
                _ => { }),
            new LiveSessionConnectOptions(
                true, "127.0.0.1", 9000, "parity", "parity"),
            runtime: Runtime);
    }

    /// <summary>Which client this is, for failure messages.</summary>
    internal string Name { get; }

    internal GameRuntimeDependencies Dependencies { get; }

    internal GameRuntime Runtime { get; }

    internal LiveSessionHost Session { get; }

    internal ParitySessionOperations Operations { get; }

    /// <summary>What a plugin sees. Built by the host, not by the harness.</summary>
    internal abstract IPluginHost Host { get; }

    /// <summary>What the binding pass said about seams this arm left empty.</summary>
    internal abstract IReadOnlyList<string> Warnings { get; }

    /// <summary>Opens the session and walks it into the world.</summary>
    internal void EnterWorld()
    {
        _ = Session.Start(Runtime.Generation);
        Runtime.SyncLifecycleEmission();
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
    internal virtual void Advance()
    {
        _ = Runtime.Clock.Advance(TickSeconds);
        _body?.Drive();
        _frame?.AdvanceBeforeNetwork((float)TickSeconds);
        Runtime.Session.Tick();
        _frame?.RunPostNetworkCommandPhase();
        OnAdvanced();
    }

    /// <summary>
    /// This host's own local-player frame driver, over this host's own frame
    /// host. Both are the classes the real client builds.
    /// </summary>
    protected abstract RuntimeLocalPlayerFrameController CreateFrameController();

    /// <summary>Whatever this host does once per step beyond ticking.</summary>
    protected virtual void OnAdvanced()
    {
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
    }

    protected virtual void DisposeHost()
    {
    }

    /// <summary>
    /// Inbound routing is not what these scenarios drive: they hand the
    /// runtime what the server said directly, so both arms see exactly the
    /// same thing at exactly the same step.
    /// </summary>
    private sealed class InertEventRoute : ILiveSessionEventRouting
    {
        public void Attach()
        {
        }

        public void Dispose()
        {
        }
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
