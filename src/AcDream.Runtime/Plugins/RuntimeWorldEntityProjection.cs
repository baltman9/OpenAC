using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Plugins;

/// <summary>
/// What a plugin sees in the world, read off the runtime's own object
/// directory. Every client answers the plugin-facing object list and the
/// notification that an object appeared from one of these, so the
/// membership, the ids and the order of the notifications are the same
/// wherever a plugin runs -- including on a client that draws nothing, where
/// there is no drawn-entity list to read instead.
/// <para>
/// An object's identity here is the one the rest of the plugin surface uses:
/// the id the client gave the object locally, and the setup the server named
/// for it.
/// </para>
/// </summary>
public sealed class RuntimeWorldEntityProjection
    : IPluginWorldEntities, IRuntimeEventObserver, IDisposable
{
    private readonly GameRuntime _runtime;
    private readonly Action<string>? _report;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private Subscription[] _liveSnapshot = [];
    private bool _disposed;

    /// <summary>
    /// Starts reading the world off this runtime's object directory.
    /// </summary>
    /// <param name="runtime">The session whose objects are reported.</param>
    /// <param name="report">
    /// Where a plugin handler that threw is named. A plugin's failure is its
    /// own and never stops the client telling the next one, but a client that
    /// says nothing about it leaves a plugin author with an overlay that
    /// silently stops updating and no idea why.
    /// </param>
    public RuntimeWorldEntityProjection(
        GameRuntime runtime,
        Action<string>? report = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _report = report;
        _subscription = runtime.Subscribe(this);
    }

    /// <summary>
    /// A directory entry paired with the snapshot handed to a plugin. The
    /// identity comes along so a replay can check the entry is still the one
    /// it read, rather than a later object under a reused id.
    /// </summary>
    private readonly record struct ReplayEntity(
        RuntimeEntityIdentity Identity,
        WorldEntitySnapshot Snapshot);

    private sealed class Subscription(Action<WorldEntitySnapshot> handler)
    {
        internal Action<WorldEntitySnapshot> Handler { get; } = handler;

        internal Queue<ReplayEntity> Pending { get; } = new();

        internal HashSet<RuntimeEntityIdentity> Delivered { get; } = [];

        internal bool Replaying { get; set; } = true;

        internal bool Active { get; set; } = true;
    }

    /// <summary>
    /// A barrier a test can use to interleave a change with a replay's read
    /// of the directory, which is how the replay's one-delivery rule is
    /// proven rather than asserted.
    /// </summary>
    internal Action? ReplayCapturedForTest { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<WorldEntitySnapshot> Entities
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var visitor = new SnapshotVisitor(_runtime);
            _runtime.Entities.Visit(visitor);
            return visitor.Items.Select(static item => item.Snapshot).ToArray();
        }
    }

    /// <inheritdoc />
    public void Subscribe(Action<WorldEntitySnapshot> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var subscription = new Subscription(handler);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions.Add(subscription);
        }

        // Replay outside the lock, then drain whatever arrived while it ran,
        // so one monotonic order reaches the handler and nothing is handed
        // to it twice.
        var visitor = new SnapshotVisitor(_runtime, ReplayCapturedForTest);
        _runtime.Entities.Visit(visitor);
        ReplayEntity[] replay = visitor.Items.ToArray();

        foreach (ReplayEntity item in replay)
        {
            lock (_gate)
            {
                if (!subscription.Active)
                    return;
                if (!_runtime.Entities.TryGet(
                        item.Identity.ServerGuid,
                        out RuntimeEntitySnapshot current)
                    || current.Identity != item.Identity
                    || !subscription.Delivered.Add(item.Identity))
                {
                    continue;
                }
            }

            Invoke(subscription.Handler, item.Snapshot);
        }

        while (true)
        {
            ReplayEntity pending;
            lock (_gate)
            {
                if (!subscription.Active)
                    return;
                if (!subscription.Pending.TryDequeue(out pending))
                {
                    subscription.Replaying = false;
                    subscription.Delivered.Clear();
                    RebuildLiveSnapshotLocked();
                    return;
                }
                if (!subscription.Delivered.Add(pending.Identity))
                    continue;
            }

            Invoke(subscription.Handler, pending.Snapshot);
        }
    }

    /// <inheritdoc />
    public void Unsubscribe(Action<WorldEntitySnapshot> handler)
    {
        if (handler is null)
            return;
        lock (_gate)
        {
            for (int index = _subscriptions.Count - 1; index >= 0; index--)
            {
                Subscription subscription = _subscriptions[index];
                if (subscription.Handler != handler)
                    continue;
                subscription.Active = false;
                subscription.Pending.Clear();
                subscription.Delivered.Clear();
                _subscriptions.RemoveAt(index);
                if (!subscription.Replaying)
                    RebuildLiveSnapshotLocked();
                break;
            }
        }
    }

    /// <summary>
    /// An object entering the directory is an object appearing in the world.
    /// Nothing else the directory reports is one: a rebucketing or a property
    /// change is the same object, and a withdrawal or a deletion is it
    /// leaving, which shows up as the entry dropping out of the object list.
    /// </summary>
    public void OnEntity(in RuntimeEntityDelta delta)
    {
        if (delta.Change != RuntimeEntityChange.Registered)
            return;

        Subscription[] toNotify;
        var pending = new ReplayEntity(
            delta.Entity.Identity,
            Convert(_runtime, delta.Entity));
        lock (_gate)
        {
            if (_disposed)
                return;
            foreach (Subscription subscription in _subscriptions)
            {
                if (subscription.Active && subscription.Replaying)
                    subscription.Pending.Enqueue(pending);
            }
            toNotify = _liveSnapshot;
        }

        foreach (Subscription subscription in toNotify)
            Invoke(subscription.Handler, pending.Snapshot);
    }

    /// <inheritdoc />
    public void OnChat(in RuntimeChatDelta delta) { }

    /// <inheritdoc />
    public void OnMovement(in RuntimeMovementDelta delta) { }

    /// <inheritdoc />
    public void OnPortal(in RuntimePortalDelta delta) { }

    /// <inheritdoc />
    public void OnCombat(in RuntimeCombatDelta delta) { }

    /// <inheritdoc />
    public void OnCommand(in RuntimeCommandDelta delta) { }

    /// <inheritdoc />
    public void OnInventory(in RuntimeInventoryDelta delta) { }

    /// <inheritdoc />
    public void OnLifecycle(in RuntimeLifecycleDelta delta) { }

    /// <summary>Stops reading the directory and drops every handler.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            _disposed = true;
            foreach (Subscription subscription in _subscriptions)
            {
                subscription.Active = false;
                subscription.Pending.Clear();
                subscription.Delivered.Clear();
            }
            _subscriptions.Clear();
            _liveSnapshot = [];
        }
        _subscription.Dispose();
    }

    private static WorldEntitySnapshot Convert(
        GameRuntime runtime,
        in RuntimeEntitySnapshot entity)
    {
        bool known = runtime.EntityObjects.Entities.TryGetActive(
            entity.Identity.ServerGuid,
            out RuntimeEntityRecord record);
        uint sourceId = known ? record.Snapshot.SetupTableId ?? 0u : 0u;
        // Where the object is, from the body that moves if it has one, and
        // from the last thing the server said otherwise. The body is the
        // answer the rest of the client works from between server updates,
        // so reading the wire snapshot here would have one plugin surface
        // giving two answers about the same object: a walk aimed at where
        // the creature was a moment ago rather than where it is.
        AcDream.Core.Physics.Position? position =
            (known ? record.PhysicsBody?.CellPosition : null)
            ?? entity.Position;
        return new WorldEntitySnapshot(
            entity.Identity.LocalEntityId,
            sourceId,
            position?.Frame.Origin ?? default,
            position?.Frame.Orientation
                ?? System.Numerics.Quaternion.Identity);
    }

    private void Invoke(
        Action<WorldEntitySnapshot> handler,
        WorldEntitySnapshot snapshot)
    {
        // One plugin's failure is its own: it does not stop the client
        // telling the next one about the same object. It is said out loud all
        // the same.
        try
        {
            handler(snapshot);
        }
        catch (Exception error)
        {
            _report?.Invoke(
                "plugin world objects: a handler threw and was skipped: "
                + error);
        }
    }

    private sealed class SnapshotVisitor(
        GameRuntime runtime,
        Action? captureBarrier = null)
        : IRuntimeEntityVisitor
    {
        private Action? _captureBarrier = captureBarrier;

        internal List<ReplayEntity> Items { get; } =
            new(runtime.Entities.Count);

        public void Visit(in RuntimeEntitySnapshot entity)
        {
            Items.Add(new ReplayEntity(
                entity.Identity,
                Convert(runtime, entity)));
            Interlocked.Exchange(ref _captureBarrier, null)?.Invoke();
        }
    }

    private void RebuildLiveSnapshotLocked() =>
        _liveSnapshot = _subscriptions
            .Where(static subscription =>
                subscription.Active && !subscription.Replaying)
            .ToArray();
}
