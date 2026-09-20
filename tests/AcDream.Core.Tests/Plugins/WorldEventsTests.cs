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
