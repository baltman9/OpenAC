using System.Numerics;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

/// <summary>
/// The spawn stream and the replay a late handler gets belong to the one
/// producer that reads the runtime's object directory; what is tested here is
/// that this type hands handlers straight to it and keeps none of its own.
/// The producer's own rules -- replay, exactly-once delivery, order under a
/// concurrent registration -- are tested where it lives.
/// </summary>
public class WorldEventsTests
{
    private static WorldEntitySnapshot S(uint id) => new(
        id,
        SourceId: 0x01000000u,
        Position: Vector3.Zero,
        Rotation: Quaternion.Identity);

    [Fact]
    public void RecallLocationRevisionCanRejectStaleState()
    {
        var location = new PluginRecallLocation(
            PluginRecallKind.House,
            default,
            "House",
            2,
            true);
        Assert.False(location.IsStaleComparedTo(1));
        Assert.True(location.IsStaleComparedTo(2));
    }

    [Fact]
    public void PortalTransitionRevisionCanRejectStaleNotifications()
    {
        var transition = new PluginPortalTransition(6, 2, 3u, true, true, false, false);

        Assert.False(transition.IsStaleComparedTo(5));
        Assert.True(transition.IsStaleComparedTo(6));
        Assert.True(transition.IsStaleComparedTo(7));
        Assert.False(transition.IsTerminal);
        Assert.True((transition with { IsCompleted = true }).IsTerminal);
        Assert.True((transition with { IsCancelled = true }).IsTerminal);
    }

    [Fact]
    public void ObjectChangeRevisionCanRejectStaleNotifications()
    {
        var change = new PluginObjectChange(1u, PluginObjectChangeKind.Updated)
        {
            Revision = 4,
        };

        Assert.False(change.IsStaleComparedTo(3));
        Assert.True(change.IsStaleComparedTo(4));
        Assert.True(change.IsStaleComparedTo(5));
        Assert.False(new PluginObjectChange(1u, PluginObjectChangeKind.Updated)
            .IsStaleComparedTo(5));
    }

    [Fact]
    public void PortalTransition_AssignsMonotonicRevisions()
    {
        var events = new WorldEvents();
        var seen = new List<PluginPortalTransition>();
        events.PortalTransition += seen.Add;

        events.FirePortalTransition(new PluginPortalTransition(0, 7, 0x1234u, true, false, false, false));
        events.FirePortalTransition(new PluginPortalTransition(0, 7, 0x1234u, true, true, false, false));

        Assert.Equal([1L, 2L], seen.Select(static transition => transition.Revision));
        Assert.All(seen, static transition => Assert.Equal(7L, transition.Generation));
    }

    [Fact]
    public void ObjectChanged_AssignsMonotonicSessionRevisions()
    {
        var events = new WorldEvents();
        var seen = new List<PluginObjectChange>();
        events.ObjectChanged += seen.Add;
        var current = new PluginWorldObject(
            1u, 100u, "Portal", PluginObjectClass.Portal, 0u, 0u, 0u)
        {
            Capabilities = PluginObjectCapabilities.Interactable
                | PluginObjectCapabilities.Portal,
        };

        events.FireObjectChanged(new PluginObjectChange(1, PluginObjectChangeKind.Created)
        {
            Current = current,
        });
        events.FireObjectChanged(new PluginObjectChange(1, PluginObjectChangeKind.Updated)
        {
            Current = current with { Name = "Updated portal" },
        });
        events.FireObjectChanged(new PluginObjectChange(1, PluginObjectChangeKind.Released));

        Assert.Equal([1L, 2L, 3L], seen.Select(static change => change.Revision));
        Assert.Equal("Updated portal", seen[1].Current?.Name);
        Assert.True(seen[1].Current?.CanActivate);
        Assert.Equal(
            PluginObjectChangeFields.Identity,
            seen[1].ChangedFields);
        Assert.Equal(
            PluginObjectChangeFields.Lifecycle,
            seen[2].ChangedFields);
        Assert.Null(seen[2].Current);
        Assert.All(seen, static change => Assert.Equal(1u, change.ObjectId));
    }

    [Fact]
    public void NavigationChanged_DeliversAndUnsubscribes()
    {
        var events = new WorldEvents();
        var seen = new List<PluginGoToReport>();
        Action<PluginGoToReport> handler = seen.Add;
        events.NavigationChanged += handler;

        events.FireNavigationChanged(new PluginGoToReport(4, PluginGoToState.Walking, 9, 12f, 1, "walking")
        {
            Revision = 2,
        });
        events.NavigationChanged -= handler;
        events.FireNavigationChanged(new PluginGoToReport(4, PluginGoToState.Arrived, 9, 0f, 1, "arrived")
        {
            Revision = 3,
        });

        var report = Assert.Single(seen);
        Assert.Equal(2, report.Revision);
        Assert.Equal(PluginGoToState.Walking, report.CurrentState);
    }

    [Fact]
    public void Subscribing_HandsTheHandlerToTheProducer()
    {
        var events = new WorldEvents();
        var producer = new RecordingWorldEntities();
        events.BindWorldEntities(producer);
        var seen = new List<uint>();
        Action<WorldEntitySnapshot> handler = snapshot => seen.Add(snapshot.Id);

        events.EntitySpawned += handler;
        producer.Raise(S(1));

        Assert.Same(handler, Assert.Single(producer.Handlers));
        Assert.Equal([1u], seen);
    }

    [Fact]
    public void Unsubscribing_TakesTheHandlerBackOffTheProducer()
    {
        var events = new WorldEvents();
        var producer = new RecordingWorldEntities();
        events.BindWorldEntities(producer);
        var seen = new List<uint>();
        Action<WorldEntitySnapshot> handler = snapshot => seen.Add(snapshot.Id);

        events.EntitySpawned += handler;
        producer.Raise(S(1));
        events.EntitySpawned -= handler;
        producer.Raise(S(2));

        Assert.Empty(producer.Handlers);
        Assert.Equal([1u], seen);
    }

    /// <summary>
    /// Before a host names its producer there is nothing to hear about, and
    /// subscribing is not an error -- a plugin loaded early must not fail.
    /// </summary>
    [Fact]
    public void WithNoProducerBoundSubscribingIsAcceptedAndSilent()
    {
        var events = new WorldEvents();
        var seen = new List<uint>();
        Action<WorldEntitySnapshot> handler = snapshot => seen.Add(snapshot.Id);

        events.EntitySpawned += handler;
        events.EntitySpawned -= handler;

        Assert.Empty(seen);
    }

    [Fact]
    public void ASecondProducerIsRefused()
    {
        var events = new WorldEvents();
        events.BindWorldEntities(new RecordingWorldEntities());

        Assert.Throws<InvalidOperationException>(() =>
            events.BindWorldEntities(new RecordingWorldEntities()));
    }

    [Fact]
    public void ANullHandlerIsRefusedRatherThanPassedOn()
    {
        var events = new WorldEvents();
        var producer = new RecordingWorldEntities();
        events.BindWorldEntities(producer);

        Assert.Throws<ArgumentNullException>(() => events.EntitySpawned += null!);
        Assert.Empty(producer.Handlers);
    }

    private sealed class RecordingWorldEntities : IPluginWorldEntities
    {
        private readonly List<Action<WorldEntitySnapshot>> _handlers = [];

        internal IReadOnlyList<Action<WorldEntitySnapshot>> Handlers => _handlers;

        public IReadOnlyList<WorldEntitySnapshot> Entities => [];

        public void Subscribe(Action<WorldEntitySnapshot> handler) =>
            _handlers.Add(handler);

        public void Unsubscribe(Action<WorldEntitySnapshot> handler) =>
            _handlers.Remove(handler);

        internal void Raise(WorldEntitySnapshot snapshot)
        {
            foreach (Action<WorldEntitySnapshot> handler in _handlers.ToArray())
                handler(snapshot);
        }
    }
}
