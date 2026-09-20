using System.Numerics;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class WorldGameStateTests
{
    private static WorldEntitySnapshot S(uint id, uint sourceId = 0x01000000u) =>
        new(id, sourceId, Vector3.Zero, Quaternion.Identity);

    /// <summary>
    /// The objects a plugin reads are not kept here: this type has no way to
    /// be told about one, so nothing in a host can put an object in front of
    /// a plugin that the runtime does not have.
    /// </summary>
    [Fact]
    public void TheObjectListIsAnsweredByTheBoundProducer()
    {
        var state = new WorldGameState();
        var producer = new FixedWorldEntities(S(1), S(2));

        state.BindWorldEntities(producer);

        Assert.Equal([1u, 2u], state.Entities.Select(static e => e.Id));
    }

    [Fact]
    public void WithNoProducerBoundTheObjectListIsEmpty()
    {
        var state = new WorldGameState();

        Assert.Empty(state.Entities);
    }

    /// <summary>
    /// Two producers would mean two answers to the same question, which is
    /// the difference this store exists to remove.
    /// </summary>
    [Fact]
    public void ASecondProducerIsRefused()
    {
        var state = new WorldGameState();
        state.BindWorldEntities(new FixedWorldEntities(S(1)));

        Assert.Throws<InvalidOperationException>(() =>
            state.BindWorldEntities(new FixedWorldEntities(S(2))));
    }

    [Fact]
    public void AddScenery_ReplacesAnEntryUnderTheSameDrawnId()
    {
        var state = new WorldGameState();
        state.AddScenery(S(1));
        state.AddScenery(S(1, 0x01000001u));

        WorldEntitySnapshot current = Assert.Single(state.SceneryObjects);
        Assert.Equal(0x01000001u, current.SourceId);
    }

    [Fact]
    public void RemoveSceneryById_CompactsTheIndexWithoutLosingTheRest()
    {
        var state = new WorldGameState();
        state.AddScenery(S(1));
        state.AddScenery(S(2));
        state.AddScenery(S(3));

        Assert.True(state.RemoveSceneryById(2));
        state.AddScenery(S(3, 0x01000003u));

        Assert.Equal(2, state.SceneryObjects.Count);
        Assert.DoesNotContain(state.SceneryObjects, piece => piece.Id == 2);
        Assert.Equal(
            0x01000003u,
            Assert.Single(state.SceneryObjects, piece => piece.Id == 3).SourceId);
    }

    [Fact]
    public void ClearScenery_RemovesTheEntriesAndTheirIndex()
    {
        var state = new WorldGameState();
        state.AddScenery(S(1));
        state.ClearScenery();
        state.AddScenery(S(1, 0x01000001u));

        WorldEntitySnapshot current = Assert.Single(state.SceneryObjects);
        Assert.Equal(0x01000001u, current.SourceId);
    }

    /// <summary>
    /// Scenery and objects are separate stores, so an id in one is not in
    /// the other even when the two happen to collide.
    /// </summary>
    [Fact]
    public void SceneryDoesNotEnterTheObjectList()
    {
        var state = new WorldGameState();
        state.BindWorldEntities(new FixedWorldEntities(S(1)));

        state.AddScenery(S(1, 0x01000009u));

        Assert.Equal(0x01000000u, Assert.Single(state.Entities).SourceId);
        Assert.Equal(0x01000009u, Assert.Single(state.SceneryObjects).SourceId);
    }

    private sealed class FixedWorldEntities(params WorldEntitySnapshot[] entities)
        : IPluginWorldEntities
    {
        public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = entities;

        public void Subscribe(Action<WorldEntitySnapshot> handler)
        {
            foreach (WorldEntitySnapshot entity in Entities)
                handler(entity);
        }

        public void Unsubscribe(Action<WorldEntitySnapshot> handler)
        {
        }
    }
}
