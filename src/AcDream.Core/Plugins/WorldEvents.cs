using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

public sealed class WorldEvents : IEvents
{
    private readonly object _lock = new();
    private IPluginWorldEntities? _worldEntities;
    private Action<double>? _tick;
    private Action? _loginComplete;
    private Action? _logoff;
    private Action<string>? _localPlayerDied;
    private Action<PluginObjectChange>? _objectChanged;
    private Action<uint>? _containerOpened;
    private Action<uint>? _containerClosed;
    private Action<PluginConfirmation>? _confirmationRequested;

    /// <summary>
    /// Names the producer that raises <see cref="EntitySpawned"/> and
    /// replays what is already there to a handler added late. Called once,
    /// where the host builds its runtime.
    /// </summary>
    public void BindWorldEntities(IPluginWorldEntities worldEntities)
    {
        ArgumentNullException.ThrowIfNull(worldEntities);
        lock (_lock)
        {
            if (_worldEntities is not null)
                throw new InvalidOperationException(
                    "The world objects a plugin watches already have a "
                    + "producer; a second one would mean two orders of the "
                    + "same events.");
            _worldEntities = worldEntities;
        }
    }

    public event Action<double> Tick
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _tick += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _tick -= value;
        }
    }

    public event Action LoginComplete
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _loginComplete += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _loginComplete -= value;
        }
    }

    public event Action Logoff
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _logoff += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _logoff -= value;
        }
    }

    public event Action<string> LocalPlayerDied
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _localPlayerDied += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _localPlayerDied -= value;
        }
    }

    public event Action<PluginObjectChange> ObjectChanged
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _objectChanged += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _objectChanged -= value;
        }
    }

    public event Action<uint> ContainerOpened
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _containerOpened += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _containerOpened -= value;
        }
    }

    public event Action<uint> ContainerClosed
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _containerClosed += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _containerClosed -= value;
        }
    }

    public event Action<PluginConfirmation> ConfirmationRequested
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
                _confirmationRequested += value;
        }
        remove
        {
            if (value is null)
                return;
            lock (_lock)
                _confirmationRequested -= value;
        }
    }

    public void FireLoginComplete()
    {
        Action? handlers;
        lock (_lock)
            handlers = _loginComplete;
        Fire(handlers);
    }

    public void FireLogoff()
    {
        Action? handlers;
        lock (_lock)
            handlers = _logoff;
        Fire(handlers);
    }

    public void FireLocalPlayerDied(string deathMessage)
    {
        ArgumentNullException.ThrowIfNull(deathMessage);
        Action<string>? handlers;
        lock (_lock)
            handlers = _localPlayerDied;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<string>)handler)(deathMessage); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    public void FireObjectChanged(PluginObjectChange change)
    {
        Action<PluginObjectChange>? handlers;
        lock (_lock)
            handlers = _objectChanged;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginObjectChange>)handler)(change); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    public void FireContainerOpened(uint containerObjectId)
    {
        Action<uint>? handlers;
        lock (_lock)
            handlers = _containerOpened;
        FireUInt(handlers, containerObjectId);
    }

    public void FireContainerClosed(uint containerObjectId)
    {
        Action<uint>? handlers;
        lock (_lock)
            handlers = _containerClosed;
        FireUInt(handlers, containerObjectId);
    }

    public void FireConfirmationRequested(PluginConfirmation confirmation)
    {
        Action<PluginConfirmation>? handlers;
        lock (_lock)
            handlers = _confirmationRequested;
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<PluginConfirmation>)handler)(confirmation); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    private static void FireUInt(Action<uint>? handlers, uint value)
    {
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<uint>)handler)(value); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    private static void Fire(Action? handlers)
    {
        if (handlers is null)
            return;
        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    public void FireTick(double elapsedSeconds)
    {
        Action<double>? handlers;
        lock (_lock)
            handlers = _tick;
        if (handlers is null)
            return;

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            try { ((Action<double>)handler)(elapsedSeconds); }
            catch { /* plugin errors do not propagate out of event dispatch */ }
        }
    }

    /// <summary>
    /// Objects appearing in the world, from the one producer that reads the
    /// runtime's object directory: adding a handler replays what is already
    /// there before it starts receiving new ones.
    /// </summary>
    public event Action<WorldEntitySnapshot> EntitySpawned
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            IPluginWorldEntities? producer;
            lock (_lock)
                producer = _worldEntities;
            producer?.Subscribe(value);
        }
        remove
        {
            if (value is null)
                return;
            IPluginWorldEntities? producer;
            lock (_lock)
                producer = _worldEntities;
            producer?.Unsubscribe(value);
        }
    }
}
