using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The one producer of what a plugin sees in the world. Both clients answer
/// their object list and their "an object appeared" notification from this,
/// so the rules it keeps -- membership follows the object directory, a
/// handler added late is replayed what is there, and nothing is handed to a
/// handler twice -- are the rules a plugin gets on either client.
///
/// These rules used to be kept twice, once in a store the drawing side wrote
/// to and once here, and the two disagreed about membership and about ids.
/// </summary>
public sealed class RuntimeWorldEntityProjectionTests
{
    private const uint First = 0x70000001u;
    private const uint Second = 0x70000002u;

    [Fact]
    public void TheListIsWhatTheObjectDirectoryHolds()
    {
        using var host = new NoWindowGameRuntimeHost();
        using var projection = new RuntimeWorldEntityProjection(host.Runtime);

        Assert.Empty(projection.Entities);
        Register(host, First);

        WorldEntitySnapshot only = Assert.Single(projection.Entities);
        Assert.Equal(LocalId(host, First), only.Id);
        Assert.Equal(SetupId(host, First), only.SourceId);
    }

    [Fact]
    public void AHandlerAddedBeforeAnythingAppearsHearsEachAppearanceOnce()
    {
        using var host = new NoWindowGameRuntimeHost();
        using var projection = new RuntimeWorldEntityProjection(host.Runtime);
        var seen = new List<uint>();
        projection.Subscribe(snapshot => seen.Add(snapshot.Id));

        Register(host, First);
        Register(host, Second);

        Assert.Equal(
            [LocalId(host, First), LocalId(host, Second)],
            seen);
    }

    [Fact]
    public void AHandlerAddedLateIsReplayedWhatIsAlreadyThere()
    {
        using var host = new NoWindowGameRuntimeHost();
        using var projection = new RuntimeWorldEntityProjection(host.Runtime);
        Register(host, First);
        Register(host, Second);

        var seen = new List<uint>();
        projection.Subscribe(snapshot => seen.Add(snapshot.Id));

        Assert.Equal(
            [LocalId(host, First), LocalId(host, Second)],
            seen.Order().ToArray());
    }

    [Fact]
    public void AReplayedObjectIsNotHandedToTheHandlerAgainWhenSomethingNewArrives()
    {
        using var host = new NoWindowGameRuntimeHost();
        using var projection = new RuntimeWorldEntityProjection(host.Runtime);
        Register(host, First);
        var seen = new List<uint>();
        projection.Subscribe(snapshot => seen.Add(snapshot.Id));

        Register(host, Second);

        Assert.Equal(
            [LocalId(host, First), LocalId(host, Second)],
            seen);
    }

    [Fact]
    public void AnUnsubscribedHandlerHearsNothingFurther()
    {
        using var host = new NoWindowGameRuntimeHost();
        using var projection = new RuntimeWorldEntityProjection(host.Runtime);
        var seen = new List<uint>();
        void Handler(WorldEntitySnapshot snapshot) => seen.Add(snapshot.Id);
        projection.Subscribe(Handler);

        Register(host, First);
        projection.Unsubscribe(Handler);
        Register(host, Second);

        Assert.Equal([LocalId(host, First)], seen);
    }

    /// <summary>
    /// One plugin failing is its own affair: the rest of a replay still
    /// reaches it, and adding the handler does not throw.
    /// </summary>
    [Fact]
    public void AHandlerThatThrowsDuringAReplayStillGetsTheRest()
    {
        using var host = new NoWindowGameRuntimeHost();
        using var projection = new RuntimeWorldEntityProjection(host.Runtime);
        Register(host, First);
        Register(host, Second);
        uint firstId = LocalId(host, First);

        var seen = new List<uint>();
        projection.Subscribe(snapshot =>
        {
            if (snapshot.Id == firstId)
                throw new InvalidOperationException("fixture handler failure");
            seen.Add(snapshot.Id);
        });

        Assert.Equal([LocalId(host, Second)], seen);
    }

    /// <summary>
    /// The server taking an object out of the world takes it out of the list
    /// and out of what a late handler is replayed. It is not an appearance,
    /// so nobody is told it appeared.
    /// </summary>
    [Fact]
    public void AnObjectTakenOutOfTheWorldLeavesTheListAndTheReplay()
    {
        using var host = new NoWindowGameRuntimeHost();
        using var projection = new RuntimeWorldEntityProjection(host.Runtime);
        Register(host, First);
        Register(host, Second);
        var seen = new List<uint>();
        projection.Subscribe(snapshot => seen.Add(snapshot.Id));
        int announcedBefore = seen.Count;
        uint firstId = LocalId(host, First);

        Delete(host, First);

        Assert.Equal(announcedBefore, seen.Count);
        Assert.DoesNotContain(
            projection.Entities,
            entity => entity.Id == firstId);
        var late = new List<uint>();
        projection.Subscribe(snapshot => late.Add(snapshot.Id));
        Assert.Equal([LocalId(host, Second)], late);
    }

    /// <summary>
    /// The same guid sent again is a new appearance, so a handler already
    /// watching is told about it a second time.
    /// </summary>
    [Fact]
    public void TheSameGuidSentAgainIsANewAppearance()
    {
        using var host = new NoWindowGameRuntimeHost();
        using var projection = new RuntimeWorldEntityProjection(host.Runtime);
        var seen = new List<uint>();
        projection.Subscribe(snapshot => seen.Add(snapshot.Id));
        Register(host, First);
        Delete(host, First);

        Register(host, First, incarnation: 2);

        Assert.Equal(2, seen.Count);
        Assert.Single(projection.Entities);
    }

    [Fact]
    public void AfterDisposalTheListIsRefusedRatherThanAnsweredStale()
    {
        using var host = new NoWindowGameRuntimeHost();
        var projection = new RuntimeWorldEntityProjection(host.Runtime);
        Register(host, First);
        projection.Dispose();

        Assert.Throws<ObjectDisposedException>(() => projection.Entities);
    }

    private static uint LocalId(NoWindowGameRuntimeHost host, uint guid) =>
        host.Runtime.EntityObjects.Entities.TryGetActive(
            guid,
            out RuntimeEntityRecord record)
            ? record.LocalEntityId ?? 0u
            : 0u;

    private static uint SetupId(NoWindowGameRuntimeHost host, uint guid) =>
        host.Runtime.EntityObjects.Entities.TryGetActive(
            guid,
            out RuntimeEntityRecord record)
            ? record.Snapshot.SetupTableId ?? 0u
            : 0u;

    private static void Register(
        NoWindowGameRuntimeHost host,
        uint guid,
        ushort incarnation = 1)
    {
        RuntimeEntityRecord record = host.Runtime.EntityObjects
            .RegisterEntity(Spawn(guid, incarnation))
            .Canonical!;
        host.Runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false);
    }

    private static void Delete(NoWindowGameRuntimeHost host, uint guid)
    {
        Assert.True(host.Runtime.EntityObjects.TryAcceptDelete(
            new DeleteObject.Parsed(guid, InstanceSequence: 1),
            isLocalPlayer: false,
            removeRetainedObject: true,
            out RuntimeEntityDeleteAcceptance acceptance));
        host.Runtime.EntityObjects.CompleteAcceptedDelete(acceptance);
        if (acceptance.RetiredCanonical is { } retired)
        {
            Exception? failure = host.Runtime.EntityObjects
                .RetireCanonicalOnly(retired);
            Assert.Null(failure);
        }
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, ushort incarnation)
    {
        var position = new CreateObject.ServerPosition(
            0x01010001u,
            10f,
            10f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: incarnation);
        var physics = new PhysicsSpawnData(
            RawState: 0u,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            [],
            [],
            [],
            null,
            null,
            guid.ToString("X8"),
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            Useability: null,
            InstanceSequence: incarnation,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
