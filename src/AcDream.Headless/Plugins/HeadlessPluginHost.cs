using AcDream.Content;
using AcDream.Core.Items;
using AcDream.Headless.Hosting;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Navigation;
using AcDream.Runtime.Plugins;

namespace AcDream.Headless.Plugins;

internal sealed class HeadlessPluginHost
    : IPluginHost,
      IPerPluginSessionSettings,
      IGameState,
      AcDream.Core.Plugins.IPluginEventSink,
      IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> EmptySettings =
        new Dictionary<string, string>();

    private readonly GameRuntime _runtime;
    private readonly RuntimeWorldEntityProjection _worldEntities;
    private readonly object _eventGate = new();
    private readonly RuntimeAutomationSurface _automation;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
        _sessionSettingsByPlugin;
    private readonly object _tickGate = new();
    private Action<double>? _tick;
    private Action? _loginComplete;
    private Action? _logoff;
    private Action<string>? _localPlayerDied;
    private Action<PluginObjectChange>? _objectChanged;
    private Action<uint>? _containerOpened;
    private Action<uint>? _containerClosed;
    private Action<PluginConfirmation>? _confirmationRequested;
    private bool _disposed;

    internal HeadlessPluginHost(
        GameRuntime runtime,
        IPluginLogger logger,
        IPluginStorage? storage = null,
        IPluginStorage? vtankProfiles = null,
        IReadOnlyDictionary<string, Dictionary<string, string>>? sessionSettings = null,
        Func<string, bool>? submitChatText = null,
        MagicCatalog? magicCatalog = null,
        HeadlessLogoutAutomation? logout = null,
        Func<uint, bool, bool>? answerConfirmation = null,
        Func<bool>? requestGracefulStop = null,
        AcDream.Content.IDatReaderWriter? content = null,
        IGameRuntimeCommands? sessionCommands = null,
        NavigationWalkController? navigationWalk = null,
        Action<string, Exception>? pluginCommandFailed = null,
        string? dataDirectory = null,
        IReadOnlyList<string>? pluginTags = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Log = logger ?? throw new ArgumentNullException(nameof(logger));
        Storage = storage ?? NoOpPluginStorage.Instance;
        VtankProfiles = vtankProfiles ?? NoOpPluginStorage.Instance;
        _sessionSettingsByPlugin = CopySessionSettings(sessionSettings);
        Window = new HeadlessHostWindow(requestGracefulStop);
        // One factory, shared with the windowed host. The surface announces
        // this client to the other clients on this machine off the tick it is
        // built with, so the tick, the folder they find each other in and the
        // words this client answers to all have to be passed here; none of
        // them can be bound later.
        _automation = RuntimeAutomationBindings.CreateSurface(
            HeadlessAutomationCapabilities.BuildSurfaceInputs(
                new HeadlessSurfaceInputParts
                {
                    Events = this,
                    DataDirectory = dataDirectory
                        ?? AcDream.Platform.ApplicationPathSet.Resolve()
                            .DataDirectory,
                    PluginTags = pluginTags,
                }));
        // One registry, the surface's own, exactly as the windowed host does
        // it: the verbs a plugin registers and the verbs the client registers
        // for itself live together, so a line typed anywhere finds all of them.
        Commands = _automation.PluginCommands;
        if (pluginCommandFailed is not null)
            _automation.ReportPluginCommandFailuresTo(pluginCommandFailed);
        // One wiring, shared with the windowed host: what this host can lend
        // the plugin surface goes in the capability record, and the runtime
        // fills the rest.
        RuntimeAutomationBindings.Apply(
            _automation,
            runtime,
            HeadlessAutomationCapabilities.Build(new HeadlessAutomationParts
            {
                Runtime = runtime,
                Warn = Log.Warn,
                Content = content,
                MagicCatalog = magicCatalog,
                SubmitChatText = submitChatText,
                SessionCommands = sessionCommands,
                NavigationWalk = navigationWalk,
                Logout = logout,
                AnswerConfirmation = answerConfirmation,
                Events = this,
            }));
        // Which runtime happening becomes which plugin event is the shared
        // surface's job, on both clients: it watches the runtime and raises
        // them here. Watching the runtime a second time from this host would
        // mean a plugin hearing every event twice.
        // The one producer of what a plugin sees in the world, shared with
        // the client that has a window.
        _worldEntities = new RuntimeWorldEntityProjection(runtime);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
        CopySessionSettings(
            IReadOnlyDictionary<string, Dictionary<string, string>>? source)
    {
        if (source is null || source.Count == 0)
            return new Dictionary<string, IReadOnlyDictionary<string, string>>();
        var copy = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            source.Count,
            StringComparer.Ordinal);
        foreach ((string pluginId, Dictionary<string, string>? perPlugin) in source)
        {
            copy[pluginId] = perPlugin is { Count: > 0 }
                ? new Dictionary<string, string>(perPlugin, StringComparer.Ordinal)
                : EmptySettings;
        }
        return copy;
    }

    public bool HasUi => false;

    public IPluginLogger Log { get; }
    public IPluginCommandRegistry Commands { get; }
    public IPluginStorage Storage { get; }
    public IPluginStorage VtankProfiles { get; }

    /// <summary>
    /// The session's loot classifier directory, the same one the graphical
    /// client keeps: a plugin that publishes its loot rules loads headless
    /// exactly as it does with a window, and another plugin can ask it for
    /// verdicts by id.
    /// </summary>
    public IPluginLootClassifierRegistry LootClassifiers { get; } =
        new AcDream.Core.Plugins.PluginLootClassifierRegistry();
    public IGameState State => this;
    public IEvents Events => this;
    public ISelectionService Selection => _runtime.ActionOwner.Selection;
    public IAutomationSurface Automation => _automation;

    /// <summary>The runtime navigation behind <see cref="Automation"/>; the session host binds its walk controller and commands to it.</summary>
    internal RuntimeNavigationAutomation NavigationAutomation => _automation.NavigationAutomation;

    /// <summary>
    /// Offers a typed line to the one command registry, so the chat route can
    /// reach the verbs plugins and the client itself registered.
    /// </summary>
    internal bool TryHandlePluginCommand(string commandLine) =>
        _automation.TryHandlePluginCommand(commandLine);

    /// <summary>
    /// Whether a verb is already spoken for on that one registry. A front end
    /// with a verb of its own asks before answering it.
    /// </summary>
    internal bool ClaimsPluginVerb(string verb) =>
        _automation.ClaimsPluginVerb(verb);

    public IUiRegistry Ui => NoOpUiRegistry.Instance;

    public IHostWindow Window { get; }

    public IReadOnlyDictionary<string, string> SessionSettings => EmptySettings;

    public IReadOnlyDictionary<string, string> SessionSettingsFor(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _sessionSettingsByPlugin.TryGetValue(
            pluginId,
            out IReadOnlyDictionary<string, string>? settings)
            ? settings
            : EmptySettings;
    }

    public event Action<double> Tick
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _tick += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _tick -= value;
        }
    }

    internal void FireTick(double elapsedSeconds)
    {
        // The surface polls its own owners off this tick, as the first thing
        // subscribed to it, which is what the windowed client does too. It is
        // not polled separately here: two polls a tick on one client and one
        // on the other is a difference in its own right.
        Action<double>? handlers;
        lock (_tickGate)
            handlers = _tick;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<double>)handler)(elapsedSeconds);
            }
            catch (Exception error)
            {
                // Plugin errors don't propagate out of event dispatch — but
                // they are no longer invisible.
                Log.Warn($"Plugin tick handler threw: {error}");
            }
        }
    }

    internal Action? ReplayCapturedForTest
    {
        get => _worldEntities.ReplayCapturedForTest;
        set => _worldEntities.ReplayCapturedForTest = value;
    }

    public IReadOnlyList<WorldEntitySnapshot> Entities
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _worldEntities.Entities;
        }
    }

    public IReadOnlyList<ContractSnapshot> Contracts
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return AcDream.Runtime.Gameplay.ContractPluginProjection.Project(
                _runtime.ContractsOwner.View,
                catalog: null,
                now: DateTime.UtcNow);
        }
    }

    public event Action<WorldEntitySnapshot> EntitySpawned
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            ObjectDisposedException.ThrowIf(_disposed, this);
            _worldEntities.Subscribe(value);
        }
        remove
        {
            if (value is null)
                return;
            _worldEntities.Unsubscribe(value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_eventGate)
            _disposed = true;
        _worldEntities.Dispose();
        lock (_tickGate)
        {
            _tick = null;
            _loginComplete = null;
            _logoff = null;
            _localPlayerDied = null;
            _objectChanged = null;
            _containerOpened = null;
            _containerClosed = null;
            _confirmationRequested = null;
        }
        _automation.Dispose();
    }

    /// <summary>
    /// The character has arrived in the world. Raised by the shared surface,
    /// which is what decides that a lifecycle change means this.
    /// </summary>
    public void FireLoginComplete() => FireLifecycle(arrived: true);

    /// <summary>The character has left the world.</summary>
    public void FireLogoff() => FireLifecycle(arrived: false);

    private void FireLifecycle(bool arrived)
    {
        Action? handlers;
        lock (_tickGate)
            handlers = arrived ? _loginComplete : _logoff;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception error)
            {
                Log.Warn($"Plugin lifecycle handler threw: {error}");
            }
        }
    }

    public event Action LoginComplete
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _loginComplete += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _loginComplete -= value;
        }
    }

    public event Action Logoff
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _logoff += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _logoff -= value;
        }
    }

    public event Action<string> LocalPlayerDied
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _localPlayerDied += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _localPlayerDied -= value;
        }
    }

    /// <summary>The character died, with the message the server sent.</summary>
    /// <param name="deathMessage">What the server said about the death.</param>
    public void FireLocalPlayerDied(string deathMessage)
    {
        Action<string>? handlers;
        lock (_tickGate)
            handlers = _localPlayerDied;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)handler)(deathMessage);
            }
            catch (Exception error)
            {
                Log.Warn($"Plugin death handler threw: {error}");
            }
        }
    }

    public event Action<PluginObjectChange> ObjectChanged
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _objectChanged += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _objectChanged -= value;
        }
    }

    public event Action<uint> ContainerOpened
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _containerOpened += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _containerOpened -= value;
        }
    }

    public event Action<uint> ContainerClosed
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _containerClosed += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _containerClosed -= value;
        }
    }

    public event Action<PluginConfirmation> ConfirmationRequested
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_tickGate)
                _confirmationRequested += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_tickGate)
                _confirmationRequested -= value;
        }
    }

    /// <summary>
    /// An object appeared, moved, changed or went away. Which runtime
    /// happening that was -- an object arriving, an inventory move, an
    /// answered description -- is decided by the shared surface, so both
    /// clients report the same kind for the same happening.
    /// </summary>
    /// <param name="change">Which object, and what happened to it.</param>
    public void FireObjectChanged(PluginObjectChange change)
    {
        Action<PluginObjectChange>? handlers;
        lock (_tickGate)
            handlers = _objectChanged;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginObjectChange>)handler)(change); }
            catch (Exception error)
            {
                Log.Warn($"Plugin object-change handler threw: {error}");
            }
        }
    }

    /// <summary>A container the character can see inside was opened.</summary>
    /// <param name="containerObjectId">The container that opened.</param>
    public void FireContainerOpened(uint containerObjectId)
    {
        Action<uint>? handlers;
        lock (_tickGate)
            handlers = _containerOpened;
        FireContainer(handlers, containerObjectId);
    }

    /// <summary>A container the character could see inside was closed.</summary>
    /// <param name="containerObjectId">The container that closed.</param>
    public void FireContainerClosed(uint containerObjectId)
    {
        Action<uint>? handlers;
        lock (_tickGate)
            handlers = _containerClosed;
        FireContainer(handlers, containerObjectId);
    }

    private void FireContainer(Action<uint>? handlers, uint value)
    {
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<uint>)handler)(value); }
            catch (Exception error)
            {
                Log.Warn($"Plugin container handler threw: {error}");
            }
        }
    }

    /// <summary>
    /// Called by the session host whenever it records a confirmation
    /// request. It goes through the shared surface rather than straight to
    /// the handlers, so this client raises it by the same road as the one
    /// with a window.
    /// </summary>
    internal void RaiseConfirmationRequested(PluginConfirmation confirmation) =>
        _automation.RaiseConfirmationRequested(confirmation);

    /// <summary>The player is being asked to accept or decline something.</summary>
    /// <param name="confirmation">What is being asked, and under which id.</param>
    public void FireConfirmationRequested(PluginConfirmation confirmation)
    {
        Action<PluginConfirmation>? handlers;
        lock (_tickGate)
            handlers = _confirmationRequested;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginConfirmation>)handler)(confirmation); }
            catch (Exception error)
            {
                Log.Warn($"Plugin confirmation handler threw: {error}");
            }
        }
    }
}
