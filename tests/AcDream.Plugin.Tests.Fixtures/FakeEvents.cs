// Copyright (c) OpenAC contributors.
// Distributed under the terms of the MIT license.

using AcDream.Plugin.Abstractions;

namespace AcDream.Plugin.Tests.Fixtures;

/// <summary>
/// A settable <see cref="IEvents"/> for tests. Handlers registered through
/// this surface are kept on the test's thread and are invoked synchronously
/// when a test calls the corresponding <c>Raise*</c> method. No handler is
/// ever invoked concurrently, and an exception thrown by one handler does
/// not prevent other handlers from running.
/// </summary>
public sealed class FakeEvents : IEvents
{
    private readonly object _gate = new();
    private Action<double>? _tick;
    private Action<PluginGoToReport>? _navigationChanged;
    private Action<PluginObjectChange>? _objectChanged;
    private Action<PluginPortalTransition>? _portalTransition;
    private Action<PluginItemUseCompletion>? _itemUseCompleted;
    private Action<uint>? _containerOpened;
    private Action<uint>? _containerClosed;
    private Action<PluginConfirmation>? _confirmationRequested;
    private readonly EventMulticaster<WorldEntitySnapshot> _entitySpawned = new();

    /// <summary>
    /// When set, this action is called for every <see cref="RaiseTick"/>
    /// invocation before any registered tick handlers fire. Useful for
    /// advancing a fake clock or polling world state atomically.
    /// </summary>
    public Action<double>? BeforeTick { get; set; }

    // ── IEvents ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public event Action<WorldEntitySnapshot> EntitySpawned
    {
        add => _entitySpawned.Add(value);
        remove => _entitySpawned.Remove(value);
    }

    /// <inheritdoc/>
    public event Action<double> Tick
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _tick += value;
        }
        remove
        {
            if (value is null) return;
            lock (_gate) _tick -= value;
        }
    }

    /// <inheritdoc/>
    public event Action LoginComplete
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public event Action Logoff
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public event Action<string> LocalPlayerDied
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    public event Action<PluginObjectChange> ObjectChanged
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _objectChanged += value;
        }
        remove
        {
            if (value is null) return;
            lock (_gate) _objectChanged -= value;
        }
    }

    /// <inheritdoc/>
    /// <inheritdoc/>
    public event Action<PluginPortalTransition> PortalTransition
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _portalTransition += value;
        }
        remove
        {
            if (value is null) return;
            lock (_gate) _portalTransition -= value;
        }
    }

    /// <inheritdoc/>
    /// <inheritdoc/>
    public event Action<PluginItemUseCompletion> ItemUseCompleted
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _itemUseCompleted += value;
        }
        remove
        {
            if (value is null) return;
            lock (_gate) _itemUseCompleted -= value;
        }
    }

    /// <inheritdoc/>
    public event Action<PluginGoToReport> NavigationChanged
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _navigationChanged += value;
        }
        remove
        {
            if (value is null) return;
            lock (_gate) _navigationChanged -= value;
        }
    }

    /// <inheritdoc/>
    public event Action<uint> ContainerOpened
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _containerOpened += value;
        }
        remove
        {
            if (value is null) return;
            lock (_gate) _containerOpened -= value;
        }
    }

    /// <inheritdoc/>
    public event Action<uint> ContainerClosed
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _containerClosed += value;
        }
        remove
        {
            if (value is null) return;
            lock (_gate) _containerClosed -= value;
        }
    }

    /// <inheritdoc/>
    public event Action<PluginConfirmation> ConfirmationRequested
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate) _confirmationRequested += value;
        }
        remove
        {
            if (value is null) return;
            lock (_gate) _confirmationRequested -= value;
        }
    }

    // ── Raise methods for tests ───────────────────────────────────────────

    /// <summary>
    /// Fires <see cref="EntitySpawned"/> for every listener.
    /// </summary>
    public void RaiseEntitySpawned(WorldEntitySnapshot snapshot) =>
        _entitySpawned.Fire(snapshot);

    /// <summary>
    /// Fires <see cref="Tick"/> for every listener.
    /// Handlers run synchronously on the caller's thread. An exception
    /// thrown by one handler is caught and does not stop the remaining
    /// handlers from running.
    /// </summary>
    public void RaiseTick(double elapsedSeconds)
    {
        BeforeTick?.Invoke(elapsedSeconds);
        lock (_gate)
        {
            Action<double>? handlers = _tick;
            if (handlers is not null)
            {
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    try { ((Action<double>)handler)(elapsedSeconds); }
                    catch { /* swallowed, matching host behaviour */ }
                }
            }
        }
    }

    /// <summary>
    /// Fires <see cref="ObjectChanged"/> for every listener.
    /// </summary>
    public void RaiseObjectChanged(PluginObjectChange change)
    {
        lock (_gate)
        {
            Action<PluginObjectChange>? handlers = _objectChanged;
            if (handlers is not null)
            {
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    try { ((Action<PluginObjectChange>)handler)(change); }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// Fires <see cref="PortalTransition"/> for every listener.
    /// </summary>
    public void RaisePortalTransition(PluginPortalTransition transition)
    {
        lock (_gate)
        {
            Action<PluginPortalTransition>? handlers = _portalTransition;
            if (handlers is not null)
            {
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    try { ((Action<PluginPortalTransition>)handler)(transition); }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// Fires <see cref="ItemUseCompleted"/> for every listener.
    /// </summary>
    public void RaiseItemUseCompleted(PluginItemUseCompletion completion)
    {
        lock (_gate)
        {
            Action<PluginItemUseCompletion>? handlers = _itemUseCompleted;
            if (handlers is not null)
            {
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    try { ((Action<PluginItemUseCompletion>)handler)(completion); }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// Fires <see cref="NavigationChanged"/> for every listener.
    /// </summary>
    public void RaiseNavigationChanged(PluginGoToReport report)
    {
        lock (_gate)
        {
            Action<PluginGoToReport>? handlers = _navigationChanged;
            if (handlers is not null)
            {
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    try { ((Action<PluginGoToReport>)handler)(report); }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// Fires <see cref="ContainerOpened"/> for every listener.
    /// </summary>
    public void RaiseContainerOpened(uint containerObjectId)
    {
        lock (_gate)
        {
            Action<uint>? handlers = _containerOpened;
            if (handlers is not null)
            {
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    try { ((Action<uint>)handler)(containerObjectId); }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// Fires <see cref="ContainerClosed"/> for every listener.
    /// </summary>
    public void RaiseContainerClosed(uint containerObjectId)
    {
        lock (_gate)
        {
            Action<uint>? handlers = _containerClosed;
            if (handlers is not null)
            {
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    try { ((Action<uint>)handler)(containerObjectId); }
                    catch { }
                }
            }
        }
    }

    /// <summary>
    /// Fires <see cref="ConfirmationRequested"/> for every listener.
    /// </summary>
    public void RaiseConfirmationRequested(PluginConfirmation confirmation)
    {
        lock (_gate)
        {
            Action<PluginConfirmation>? handlers = _confirmationRequested;
            if (handlers is not null)
            {
                foreach (Delegate handler in handlers.GetInvocationList())
                {
                    try { ((Action<PluginConfirmation>)handler)(confirmation); }
                    catch { }
                }
            }
        }
    }

    // ── Helper: thread-safe multicast helper ──────────────────────────────

    private sealed class EventMulticaster<T>
    {
        private readonly object _gate = new();
        private Action<T>? _handlers;

        internal void Add(Action<T> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            lock (_gate) _handlers += handler;
        }

        internal void Remove(Action<T> handler)
        {
            if (handler is null) return;
            lock (_gate) _handlers -= handler;
        }

        internal void Fire(T value)
        {
            Action<T>? handlers;
            lock (_gate)
                handlers = _handlers;
            if (handlers is null)
                return;
            foreach (Delegate handler in handlers.GetInvocationList())
            {
                try { ((Action<T>)handler)(value); }
                catch { }
            }
        }
    }
}