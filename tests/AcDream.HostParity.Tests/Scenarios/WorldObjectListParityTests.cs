using AcDream.Core.Items;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// What a plugin sees in the world, and what it is told as things appear:
/// the same population, the same ids, the same order, on both clients.
///
/// The client with a window used to answer this from what it was drawing --
/// membership followed the drawable, the id was the drawable's id, and the
/// landscape's own decoration was mixed in -- while the client with no window
/// read the object directory. A plugin asking the same question therefore got
/// a different answer depending on which client ran it, and an object the
/// server had sent but nothing had drawn yet was simply absent.
///
/// These scenarios drive both arms and require the transcripts to be equal,
/// and each one also asserts the effect outright: with nothing drawn on
/// either arm, two clients that both read the directory agree, and agreeing
/// is not the point.
///
/// Mutation check (2026-09-20): pointing the windowed arm's state and events
/// back at their own store instead of the runtime producer turns every one of
/// these red, because the drawn-nothing arm then answers an empty world.
/// </summary>
public sealed class WorldObjectListParityTests
{
    /// <summary>
    /// An object the server has sent is in the list on both clients, under
    /// the same id, whether or not anything is drawing it.
    /// </summary>
    [Fact]
    public void AnObjectTheServerSentIsInTheListOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            arm.Advance();

            transcript.Step("the world as a plugin reads it");
            RecordList(transcript, arm);
            Assert.Contains(
                arm.Host.State.Entities,
                entity => entity.Id == LocalId(arm, ParityWorld.Monster));
        });

    /// <summary>
    /// Creating, deleting and creating the same guid again: the object leaves
    /// the list when the server takes it out of the world, comes back when
    /// the server sends it again, and a plugin is told about each appearance
    /// once, in that order.
    /// </summary>
    [Fact]
    public void CreateDeleteAndCreateAgainReadTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            var announced = new List<uint>();
            arm.Host.Events.EntitySpawned += snapshot =>
                announced.Add(snapshot.Id);
            arm.Advance();

            transcript.Step("a creature appears");
            ParityWorld.Add(
                arm.Runtime,
                Newcomer,
                ParityWorld.PlayerX + 5f,
                ParityWorld.MonsterObject(Newcomer));
            arm.Advance();
            uint firstId = LocalId(arm, Newcomer);
            RecordPresence(transcript, arm, Newcomer);
            transcript.Record("announced.count", announced.Count);
            Assert.Contains(firstId, announced);

            transcript.Step("the server takes it out of the world");
            arm.Server.DeleteObject(Newcomer, instanceSequence: 1);
            arm.Advance();
            RecordPresence(transcript, arm, Newcomer);
            transcript.Record("announced.count", announced.Count);
            Assert.DoesNotContain(
                arm.Host.State.Entities,
                entity => entity.Id == firstId);

            transcript.Step("the server sends it again");
            ParityWorld.Add(
                arm.Runtime,
                Newcomer,
                ParityWorld.PlayerX + 6f,
                ParityWorld.MonsterObject(Newcomer));
            arm.Advance();
            RecordPresence(transcript, arm, Newcomer);
            transcript.Record("announced.count", announced.Count);
            transcript.Record("announced.last", announced[^1]);
            Assert.Contains(
                arm.Host.State.Entities,
                entity => entity.Id == LocalId(arm, Newcomer));
        });

    /// <summary>
    /// An item picked up leaves the world: it drops out of the list on both
    /// clients while the character's own record of it stays, because a
    /// carried thing is inventory rather than something standing in a cell.
    /// </summary>
    [Fact]
    public void AnItemGoingIntoAPackLeavesTheListOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            ParityWorld.StageCarriedItems(arm.Runtime);
            ParityWorld.Add(
                arm.Runtime,
                GroundItem,
                ParityWorld.PlayerX + 1f,
                GroundItemObject());
            arm.Advance();

            transcript.Step("it is lying on the ground");
            RecordPresence(transcript, arm, GroundItem);
            Assert.Contains(
                arm.Host.State.Entities,
                entity => entity.Id == LocalId(arm, GroundItem));

            transcript.Step("it goes into a pack");
            // The server takes the thing out of the world and the character's
            // own record of it names a container, which is what carrying is.
            arm.Server.DeleteObject(GroundItem, instanceSequence: 1);
            ClientObject carried = GroundItemObject();
            carried.ContainerId = ParityWorld.Player;
            arm.Runtime.InventoryOwner.Objects.AddOrUpdate(carried);
            arm.Advance();
            RecordPresence(transcript, arm, GroundItem);
            transcript.Record(
                "carried",
                arm.Runtime.InventoryOwner.Objects.Get(GroundItem)?.ContainerId
                    == ParityWorld.Player);
            Assert.DoesNotContain(
                arm.Host.State.Entities,
                entity => entity.Id == LocalId(arm, GroundItem));
        });

    /// <summary>
    /// A plugin that starts watching after the world is already populated is
    /// replayed what is there, once each, before it hears about anything new
    /// -- the same set on both clients, and the same set the list answers.
    /// </summary>
    [Fact]
    public void ALateWatcherIsReplayedTheSameWorldOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            arm.Advance();

            transcript.Step("a plugin starts watching late");
            var replayed = new List<uint>();
            arm.Host.Events.EntitySpawned += snapshot =>
                replayed.Add(snapshot.Id);
            transcript.Record("replayed.count", replayed.Count);
            Record(transcript, "replayed", replayed);
            // What it was replayed is what the list holds, with nothing
            // handed to it twice.
            Assert.Equal(
                arm.Host.State.Entities
                    .Select(static entity => entity.Id)
                    .OrderBy(static id => id)
                    .ToArray(),
                replayed.Order().ToArray());
            Assert.Equal(replayed.Count, replayed.Distinct().Count());

            transcript.Step("then something new appears");
            ParityWorld.Add(
                arm.Runtime,
                Newcomer,
                ParityWorld.PlayerX + 5f,
                ParityWorld.MonsterObject(Newcomer));
            arm.Advance();
            transcript.Record("replayed.count", replayed.Count);
            transcript.Record("replayed.last", replayed[^1]);
            Assert.Equal(LocalId(arm, Newcomer), replayed[^1]);
        });

    /// <summary>
    /// The landscape's own decoration is not in the object list, and a client
    /// that draws nothing has none of it to report either way.
    /// </summary>
    [Fact]
    public void TheDecorationOfTheLandscapeIsNotInTheObjectList() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            arm.Advance();

            transcript.Step("scenery");
            transcript.Record("scenery.count", arm.Host.State.SceneryObjects.Count);
            Assert.Empty(arm.Host.State.SceneryObjects);
        });

    /// <summary>A creature that turns up partway through a scenario.</summary>
    private const uint Newcomer = 0x50000031u;

    /// <summary>Something lying on the ground that can be picked up.</summary>
    private const uint GroundItem = 0x50000032u;

    private static ClientObject GroundItemObject() => new()
    {
        ObjectId = GroundItem,
        Name = "ground item",
        Type = ItemType.Gem,
    };

    /// <summary>
    /// The id a plugin sees for a server guid. It comes from the runtime
    /// directory on both clients, which is the point.
    /// </summary>
    private static uint LocalId(ParityArm arm, uint serverGuid) =>
        arm.Runtime.EntityObjects.Entities.TryGetActive(
            serverGuid,
            out AcDream.Runtime.Entities.RuntimeEntityRecord record)
            ? record.LocalEntityId ?? 0u
            : 0u;

    private static void RecordPresence(
        ParityTranscript transcript,
        ParityArm arm,
        uint serverGuid)
    {
        uint id = LocalId(arm, serverGuid);
        transcript.Record("id", id);
        transcript.Record(
            "listed",
            arm.Host.State.Entities.Any(entity => entity.Id == id && id != 0u));
        transcript.Record("count", arm.Host.State.Entities.Count);
    }

    private static void RecordList(ParityTranscript transcript, ParityArm arm)
    {
        IReadOnlyList<WorldEntitySnapshot> entities = arm.Host.State.Entities;
        transcript.Record("count", entities.Count);
        Record(
            transcript,
            "ids",
            entities.Select(static entity => entity.Id));
        Record(
            transcript,
            "sources",
            entities
                .OrderBy(static entity => entity.Id)
                .Select(static entity => entity.SourceId));
    }

    private static void Record(
        ParityTranscript transcript,
        string key,
        IEnumerable<uint> ids)
    {
        uint[] ordered = ids.Order().ToArray();
        for (int index = 0; index < ordered.Length; index++)
            transcript.Record($"{key}[{index}]", ordered[index]);
    }
}
