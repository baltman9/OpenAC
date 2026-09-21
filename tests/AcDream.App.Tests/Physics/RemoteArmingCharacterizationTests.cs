using System.Numerics;
using AcDream.App.Physics;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.Selection;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using DatReaderWriter.DBObjs;

namespace AcDream.App.Tests.Physics;

/// <summary>
/// What the client WITH a window does today when the server's word about
/// another creature arrives: what it binds to that creature's body, where it
/// sends the body when told to walk, and what it sticks the body to.
///
/// These are written as a before-and-after: every answer here was recorded
/// against the window's own copy of the arming code and must be the same
/// answer once the arming is the shared one. They are deliberately about
/// answers a plugin or the drawn world can see -- a target, a destination, a
/// position -- and not about which class produced them.
///
/// The destination cases are here for a second reason. A walk order names a
/// place as "this far from the corner of this landblock", and turning that
/// into a position needs a centre to measure from. The client with a window
/// keeps its own centre for the world it streams, and that centre and the
/// shared one can legitimately disagree while the character is crossing
/// between landblocks or before either is known. These pin what the window
/// answers in each of those windows so the disagreement is a recorded fact
/// rather than a surprise.
///
/// Mutation checks (2026-09-20), run after the arming moved:
/// * making the armed body report its own position rather than where this
///   client has it drawn turned
///   <see cref="ArmingABodyGivesItAHostThatReportsWhereTheBodyIsDrawn"/> red;
/// * dropping "and I am drawing it" from what this client offers the arming
///   turned <see cref="ABodyTheWindowIsNotDrawingIsLeftUnarmed"/> red;
/// * restoring each turned them green again.
/// </summary>
public sealed class RemoteArmingCharacterizationTests
{
    private const uint TargetGuid = 0x70000191u;
    private const uint MoverGuid = 0x70000192u;

    /// <summary>A cell three landblocks east and two north of the corner.</summary>
    private const uint DestinationCell = 0x03020001u;

    private const float WireX = 10f;
    private const float WireY = 12f;
    private const float WireZ = 5f;

    /// <summary>
    /// The place a walk order names, worked out the way the window works it
    /// out: the wire origin carried into the world by the centre the window
    /// streams around. Three centres, because the window really holds all
    /// three at different moments -- settled, a standing guess, and none.
    /// </summary>
    [Theory]
    [InlineData(1, 1, true, WireX + (3 - 1) * 192f, WireY + (2 - 1) * 192f)]
    [InlineData(3, 2, false, WireX, WireY)]
    [InlineData(0, 0, false, WireX + 3 * 192f, WireY + 2 * 192f)]
    public void AWalkToAPlaceIsMeasuredFromTheCentreTheWindowStreamsAround(
        int centreX,
        int centreY,
        bool settled,
        float expectedX,
        float expectedY)
    {
        var origin = new LiveWorldOriginState();
        if (settled)
            Assert.True(origin.TryInitialize(centreX, centreY));
        else if (centreX != 0 || centreY != 0)
            origin.SetPlaceholder(centreX, centreY);
        Assert.Equal(settled, origin.IsKnown);

        Vector3 destination = MoveToMath.OriginToWorld(
            DestinationCell,
            WireX,
            WireY,
            WireZ,
            origin.CenterX,
            origin.CenterY);

        Assert.Equal(new Vector3(expectedX, expectedY, WireZ), destination);
    }

    [Fact]
    public void AWalkAtACreatureSeeksThatCreatureAtItsOwnGirthAndHeight()
    {
        var world = new ArmingWorld();
        world.Origin.Recenter(1, 1);
        world.DrawnPositionOf(TargetGuid, new Vector3(40f, 41f, 6f));

        MoveToManager moveTo = world.RouteWalkAtTheTarget();

        Assert.Equal(MovementType.MoveToObject, moveTo.MovementTypeState);
        Assert.Equal(TargetGuid, moveTo.SoughtObjectId);
        Assert.Equal(TargetGuid, moveTo.TopLevelObjectId);
        (float radius, float height) = world.ShapeOf(TargetGuid);
        Assert.Equal(radius, moveTo.SoughtObjectRadius);
        Assert.Equal(height, moveTo.SoughtObjectHeight);
    }

    [Fact]
    public void AWalkAtSomethingWithNoDrawnBodyDegradesToTheWirePlace()
    {
        var world = new ArmingWorld();
        world.Origin.Recenter(1, 1);

        MoveToManager moveTo =
            world.RouteWalkAtTheTarget(targetGuid: 0x70009999u);

        Assert.NotEqual(MovementType.MoveToObject, moveTo.MovementTypeState);
        Assert.Equal(0u, moveTo.SoughtObjectId);
    }

    [Fact]
    public void StickingToSomethingOnTheWireSticksToIt()
    {
        var world = new ArmingWorld();
        EntityPhysicsHost host = world.MinimalHostFor(MoverGuid);

        world.Motion.StickToObjectFromWire(host, TargetGuid);

        Assert.Equal(TargetGuid, host.PositionManager.GetStickyObjectId());
    }

    [Fact]
    public void StickingToSomethingWithNoDrawnBodySticksToNothing()
    {
        var world = new ArmingWorld();
        EntityPhysicsHost host = world.MinimalHostFor(MoverGuid);

        world.Motion.StickToObjectFromWire(host, 0x70009999u);

        Assert.Equal(0u, host.PositionManager.GetStickyObjectId());
    }

    [Fact]
    public void ArmingABodyGivesItAHostThatReportsWhereTheBodyIsDrawn()
    {
        var world = new ArmingWorld();
        RemoteMotion remote =
            world.Live.GetOrCreateRemoteMotionRuntime(MoverGuid);
        remote.Body.Position = new Vector3(1f, 2f, 3f);
        var drawn = new Vector3(70f, 71f, 8f);
        world.DrawnPositionOf(MoverGuid, drawn);

        world.Motion.EnsureRemoteMotionBindings(remote, ae: null, MoverGuid);

        Assert.NotNull(remote.Host);
        Assert.Equal(drawn, remote.Host!.Position.Frame.Origin);
        Assert.True(world.Live.TryGetRecord(
            MoverGuid, out LiveEntityRecord record));
        Assert.Equal(record.FullCellId, remote.Host.Position.ObjCellId);
    }

    /// <summary>
    /// The second call finds the body already armed and hands back the same
    /// host, rather than building a second one under the same body.
    /// </summary>
    [Fact]
    public void ArmingABodyTwiceLeavesItWithTheOneHost()
    {
        var world = new ArmingWorld();
        RemoteMotion remote =
            world.Live.GetOrCreateRemoteMotionRuntime(MoverGuid);
        world.DrawnPositionOf(MoverGuid, new Vector3(5f, 5f, 5f));

        world.Motion.EnsureRemoteMotionBindings(remote, ae: null, MoverGuid);
        AcDream.Core.Physics.Motion.IPhysicsObjHost? first = remote.Host;
        world.Motion.EnsureRemoteMotionBindings(remote, ae: null, MoverGuid);

        Assert.NotNull(first);
        Assert.Same(first, remote.Host);
    }

    /// <summary>
    /// A body the window has no drawn form for is left unarmed. This is the
    /// stronger of the two eligibility tests and the reason the shared arming
    /// has to ask the host rather than the record.
    /// </summary>
    [Fact]
    public void ABodyTheWindowIsNotDrawingIsLeftUnarmed()
    {
        var world = new ArmingWorld();
        RemoteMotion remote =
            world.Live.GetOrCreateRemoteMotionRuntime(MoverGuid);
        world.Undraw(MoverGuid);

        world.Motion.EnsureRemoteMotionBindings(remote, ae: null, MoverGuid);

        Assert.Null(remote.Host);
    }

    private sealed class ArmingWorld
    {
        private readonly GpuWorldState _spatial = new();

        internal ArmingWorld()
        {
            _spatial.AddLandblock(new LoadedLandblock(
                0x0101FFFFu,
                new LandBlock(),
                Array.Empty<WorldEntity>()));
            (LiveEntityRuntime live, RuntimeEntityObjectLifetime lifetime) =
                LiveEntityRuntimeFixture.CreateWithLifetime(
                    _spatial,
                    new DelegateLiveEntityResourceLifecycle(
                        _ => { }, _ => { }));
            Live = live;
            Live.RegisterAndMaterializeProjection(Spawn(TargetGuid));
            Live.RegisterAndMaterializeProjection(Spawn(MoverGuid));
            Motion = new LiveEntityMotionRuntimeController(
                Live,
                static () => null,
                new SelectionState(),
                Origin);
            // The same binding the session composition makes: this client is the
            // richer body maker, so the shared owner resolves through it.
            lifetime.Physics.BindObjectTableHostResolver(
                Motion.ResolvePhysicsHost);
            Motion.BindArming(
                LiveEntityRuntimeFixture.CreateArming(
                    lifetime, Motion.HostFacts));
        }

        internal LiveEntityRuntime Live { get; }

        internal LiveWorldOriginState Origin { get; } = new();

        internal LiveEntityMotionRuntimeController Motion { get; }

        internal RuntimePhysicsState Physics => Live.Physics;

        internal (float Radius, float Height) ShapeOf(uint guid) =>
            Physics.EntityBodyShape(guid) ?? (0f, 0f);

        internal void DrawnPositionOf(uint guid, Vector3 position)
        {
            Assert.True(Live.TryGetWorldEntity(guid, out WorldEntity entity));
            entity.SetPosition(position);
        }

        /// <summary>
        /// Takes the drawn form away and leaves the record alone. That is the
        /// state the window's own eligibility test exists to catch, and the
        /// one the record cannot answer for itself.
        /// </summary>
        internal void Undraw(uint guid)
        {
            Assert.True(Live.TryGetRecord(guid, out LiveEntityRecord record));
            record.WorldEntity = null;
        }

        internal EntityPhysicsHost MinimalHostFor(uint guid)
        {
            var host = Motion.ResolvePhysicsHost(guid) as EntityPhysicsHost;
            Assert.NotNull(host);
            return host!;
        }

        internal MoveToManager RouteWalkToAPlace() =>
            Route(WalkUpdate(targetGuid: null));

        internal MoveToManager RouteWalkAtTheTarget(
            uint targetGuid = TargetGuid) =>
            Route(WalkUpdate(targetGuid));

        private MoveToManager Route(WorldSession.EntityMotionUpdate update)
        {
            var movement = new MovementManager(new MotionInterpreter());
            movement.MoveToFactory = () => ObservableMoveTo(movement);
            Assert.True(Motion.RouteServerMoveTo(
                movement, DestinationCell, update));
            Assert.NotNull(movement.MoveTo);
            return movement.MoveTo!;
        }

        private static MoveToManager ObservableMoveTo(MovementManager movement)
        {
            var body = new PhysicsBody();
            return new MoveToManager(
                movement.Minterp,
                stopCompletely: () => { },
                getPosition: () => new Position(
                    DestinationCell, body.Position, body.Orientation),
                getHeading: () => 0f,
                setHeading: (_, _) => { },
                getOwnRadius: () => 0.48f,
                getOwnHeight: () => 1.835f,
                contact: () => true,
                isInterpolating: () => false,
                getVelocity: () => Vector3.Zero,
                getSelfId: () => MoverGuid,
                setTarget: (_, _, _, _) => { },
                clearTarget: () => { },
                getTargetQuantum: () => 0d,
                setTargetQuantum: _ => { },
                curTime: () => 0d);
        }

        private static WorldSession.EntityMotionUpdate WalkUpdate(
            uint? targetGuid) => new(
                Guid: MoverGuid,
                MotionState: new CreateObject.ServerMotionState(
                    Stance: 0x3D,
                    ForwardCommand: null,
                    MovementType: 6,
                    MoveToParameters: 0x203u,
                    MoveToSpeed: 1f,
                    MoveToRunRate: 1f,
                    MoveToPath: new CreateObject.MoveToPathData(
                        TargetGuid: targetGuid,
                        OriginCellId: DestinationCell,
                        OriginX: WireX,
                        OriginY: WireY,
                        OriginZ: WireZ,
                        DistanceToObject: 0.6f,
                        MinDistance: 0f,
                        FailDistance: 15f,
                        WalkRunThreshold: 15f,
                        DesiredHeading: 0f,
                        Bitfield: 0x203u)),
                InstanceSequence: 1,
                MovementSequence: 2,
                ServerControlSequence: 3,
                IsAutonomous: false);

        private static WorldSession.EntitySpawn Spawn(uint guid)
        {
            var position = new CreateObject.ServerPosition(
                0x01010001u, 10f, 10f, 5f, 1f, 0f, 0f, 0f);
            var timestamps = new PhysicsTimestamps(
                Position: 1,
                Movement: 1,
                State: 1,
                Vector: 1,
                Teleport: 0,
                ServerControlledMove: 1,
                ForcePosition: 0,
                ObjDesc: 1,
                Instance: 1);
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
                Guid: guid,
                Position: position,
                SetupTableId: 0x02000001u,
                AnimPartChanges: Array.Empty<CreateObject.AnimPartChange>(),
                TextureChanges: Array.Empty<CreateObject.TextureChange>(),
                SubPalettes: Array.Empty<CreateObject.SubPaletteSwap>(),
                BasePaletteId: null,
                ObjScale: null,
                Name: "creature",
                ItemType: null,
                MotionState: null,
                MotionTableId: null,
                PhysicsState: 0u,
                InstanceSequence: 1,
                MovementSequence: 1,
                ServerControlSequence: 1,
                PositionSequence: 1,
                Physics: physics);
        }
    }
}
