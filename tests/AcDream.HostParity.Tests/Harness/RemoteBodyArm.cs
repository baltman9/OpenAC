using System.Collections.Immutable;
using System.Numerics;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;
using AcDream.Runtime.Session;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.HostParity.Tests;

/// <summary>
/// One of the two clients, with a creature standing in front of it and the
/// real machinery that carries that creature's body between the server's
/// updates: the client with a window runs its own per-frame scheduler, and the
/// client without runs its own drive. Nothing below those two is duplicated —
/// the body, the arming, the animation content and the ground are the same
/// shared owners on both, built from the same numbers — so what a scenario
/// compares is the two drives and nothing else.
/// </summary>
/// <remarks>
/// What is NOT under these arms, and why:
/// * the client with a window has its own inbound sink for another creature's
///   position, which cannot be built without a drawn world. Both arms
///   therefore deliver an accepted position the way the windowless sink does:
///   classify it through the shared lifetime, route it through the shared
///   arming, and adopt the wire cell by the shared rule. That is the same
///   shared code the windowed sink calls, in the same order, and the windowed
///   sink's extra presentation writes are the part a window adds.
/// * nothing is drawn, so the windowed arm's pose composition ends at the
///   part poses the scheduler hands back.
/// </remarks>
internal abstract class RemoteBodyArm : IDisposable
{
    /// <summary>The step both arms advance by, every time.</summary>
    internal const float TickSeconds = 0.015f;

    internal const uint Player = 0x50000001u;
    internal const uint Creature = 0x70000001u;
    internal const uint Cell = 0x01010001u;
    internal const uint Landblock = 0x0101FFFFu;
    internal const uint SetupId = 0x02000001u;
    internal const uint AnimationId = 0x0300AA01u;

    /// <summary>The landblock both arms measure world positions from.</summary>
    internal const int CenterX = 1;

    internal const int CenterY = 1;

    /// <summary>The height of the flat ground both arms stand on, in metres.</summary>
    internal const float GroundZ = 5f;

    internal static readonly Vector3 Start = new(10f, 10f, GroundZ);

    /// <summary>A tenth of a metre a frame at thirty frames a second.</summary>
    internal const float AuthoredMetresPerFrame = 0.1f;

    private readonly RuntimeEntityObjectLifetime _lifetime;
    private ushort _positionSequence = 1;
    private ushort _teleportSequence;
    private bool _disposed;

    protected RemoteBodyArm(string name)
    {
        Name = name;
        _lifetime = new RuntimeEntityObjectLifetime();
        _lifetime.BindEventContext(
            static () => new RuntimeGenerationToken(1UL),
            static () => 1UL);
        _lifetime.Physics.SetPosition.BeginCollisionGeneration(Landblock, 1UL);
        AddFlatGround(_lifetime.Physics.Engine);
        _lifetime.Physics.SetPosition.CommitCollisionGeneration(
            Landblock, 1UL, ready: true);
        _lifetime.Physics.ObserveLocalWorldFrame(Cell, teleportAdvanced: false);
        _lifetime.Physics.BindSetupCollisionSource(new OneAuthoredShape());
        _lifetime.Physics.BindMotionContentSource(new WalkCycleContent());
        PlayerPosition = Start;
    }

    internal string Name { get; }

    protected RuntimeEntityObjectLifetime Lifetime => _lifetime;

    /// <summary>Where this client has the character. Both arms are told the same.</summary>
    internal Vector3 PlayerPosition { get; set; }

    /// <summary>The creature's body, once the server has said it is there.</summary>
    internal RemoteMotion Body =>
        Record.RemoteMotion as RemoteMotion
        ?? throw new InvalidOperationException(
            "The creature has no body on this client yet.");

    internal bool HasBody => Record.RemoteMotion is RemoteMotion;

    /// <summary>The creature's record on this client.</summary>
    internal abstract RuntimeEntityRecord Record { get; }

    /// <summary>The one arming, with this client's own facts hung off it.</summary>
    internal abstract RuntimeRemoteArming Arming { get; }

    /// <summary>One frame of this client's own drive.</summary>
    internal abstract void Tick(float elapsedSeconds);

    /// <summary>Brings the creature into the world as the server described it.</summary>
    internal abstract void CreatureArrives();

    /// <summary>
    /// Gives the creature a body and stands it on the ground, which is what
    /// the first word the server says about where it is does. Both clients do
    /// this through the same shared arming.
    /// </summary>
    internal void GiveTheCreatureABody()
    {
        RemoteMotion remote =
            Lifetime.Physics.GetOrCreateRemoteMotion(Record);
        remote.Body.Orientation = Quaternion.Identity;
        remote.Body.Position = Start;
        remote.Body.InWorld = true;
        remote.CellId = Cell;
        Arming.SeatBodyIfUnseated(Record, remote, Start, Cell);
        remote.LastServerPos = Start;
        remote.LastServerPosTime = 1.0;
        Lifetime.Physics.AcknowledgeSpatialProjection(Record, spatial: true);
        OnBodyGiven(remote);
    }

    /// <summary>Whatever this client hangs off a body once it has one.</summary>
    protected virtual void OnBodyGiven(RemoteMotion remote)
    {
    }

    /// <summary>
    /// The server saying where the creature is now. Both clients classify and
    /// route it through the same shared owners, in the same order.
    /// </summary>
    internal RuntimeRemoteContactArm ServerSaysTheCreatureIsAt(
        Vector3 worldPosition,
        bool teleported = false)
    {
        _positionSequence++;
        if (teleported)
            _teleportSequence++;
        var update = new WorldSession.EntityPositionUpdate(
            Guid: Creature,
            Position: new CreateObject.ServerPosition(
                Cell,
                worldPosition.X,
                worldPosition.Y,
                worldPosition.Z,
                1f,
                0f,
                0f,
                0f),
            Velocity: null,
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: 1,
            PositionSequence: _positionSequence,
            TeleportSequence: _teleportSequence,
            ForcePositionSequence: 0);
        bool accepted = Lifetime.TryApplyPosition(
            update,
            isLocalPlayer: false,
            forcePositionRotation: null,
            currentLocalVelocity: null,
            acknowledgeProjection: null,
            out PositionTimestampDisposition disposition,
            out _,
            out AcceptedPhysicsTimestamps timestamps);
        Assert.True(accepted, $"refused: disposition={disposition}");

        RemoteMotion remote = Body;
        RuntimeAuthoritativePositionRoute? route =
            Lifetime.ClassifyRemoteAcceptedPosition(
                Record,
                update,
                disposition,
                timestamps,
                Vector3.Distance(worldPosition, PlayerPosition));
        RuntimeRemoteContactRouting routing = Arming.ApplyRemoteContactRouting(
            Record,
            remote,
            route,
            worldPosition,
            Quaternion.Identity,
            willBeAdvanced: RuntimeRemoteBodyDisposition.WillAdvance(
                Lifetime.Physics,
                Record,
                remote,
                RootClockAdvances),
            runTeleportHook: static () => true);
        RuntimeRemoteSteadyStatePosition.TryArmConstraintAfterOperation(
            ToConstraintArm(routing.Arm),
            remote);
        _ = RuntimeRemoteArming.TryAdoptWireCellAfterRouting(
            remote,
            routing,
            Cell);
        return routing.Arm;
    }

    /// <summary>
    /// Whether this client says the creature's clock is running. Each client
    /// answers it from what it is presenting, which is the one part of the
    /// shared test that is really a client's own.
    /// </summary>
    protected abstract bool RootClockAdvances { get; }

    /// <summary>
    /// Where a plugin is told the creature is. Both clients must answer with
    /// the body, because on both clients there is a body being carried.
    /// </summary>
    internal AcDream.Plugin.Abstractions.PluginNavigationPosition
        PluginPosition() =>
        AcDream.Runtime.Gameplay.RuntimeWorldObjectProjection.Project(
            Record,
            Lifetime.Objects.Get(Creature),
            Player,
            Lifetime.Objects).Position;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeArm();
        _lifetime.Dispose();
    }

    protected virtual void DisposeArm()
    {
    }

    private static RuntimeRemoteAcceptedPositionArm ToConstraintArm(
        RuntimeRemoteContactArm arm) => arm switch
    {
        RuntimeRemoteContactArm.TeleportPlacement =>
            RuntimeRemoteAcceptedPositionArm.TeleportPlacement,
        RuntimeRemoteContactArm.FarSnapPlacement =>
            RuntimeRemoteAcceptedPositionArm.FarSnapPlacement,
        RuntimeRemoteContactArm.SteadyStateInterpolate =>
            RuntimeRemoteAcceptedPositionArm.NearInterpolate,
        RuntimeRemoteContactArm.AirborneSnap =>
            RuntimeRemoteAcceptedPositionArm.NearInterpolate,
        _ => RuntimeRemoteAcceptedPositionArm.UnroutedCatchUp,
    };

    private static void AddFlatGround(PhysicsEngine engine)
    {
        var table = new float[256];
        Array.Fill(table, GroundZ);
        engine.AddLandblock(
            Landblock,
            new TerrainSurface(new byte[81], table),
            Array.Empty<CellSurface>(),
            Array.Empty<PortalPlane>(),
            worldOffsetX: 0f,
            worldOffsetY: 0f);
    }

    /// <summary>The creature's creation description, identical on both arms.</summary>
    internal static WorldSession.EntitySpawn Spawn(
        PhysicsStateFlags state = PhysicsStateFlags.Gravity
            | PhysicsStateFlags.ReportCollisions
            | PhysicsStateFlags.EdgeSlide)
    {
        var position = new CreateObject.ServerPosition(
            Cell, Start.X, Start.Y, Start.Z, 1f, 0f, 0f, 0f);
        var timestamps = new PhysicsTimestamps(1, 1, 1, 1, 0, 1, 0, 1, 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)state,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: SetupId,
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
            Creature,
            position,
            SetupId,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "parity creature",
            null,
            null,
            null,
            PhysicsState: (uint)state,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    /// <summary>Any destination is serviceable; there is one landblock.</summary>
    internal sealed class AnyDestination : IRuntimeRemotePlacementServiceWindow
    {
        public bool IsWithinServiceWindow(uint landblockId) => true;
    }

    /// <summary>
    /// Animation content with one part layout and one cycle: a walk authored
    /// at a tenth of a metre a frame, thirty frames a second, which is three
    /// metres a second. Both arms read their bodies out of this one source.
    /// </summary>
    internal sealed class WalkCycleContent : IRuntimeMotionContentSource
    {
        private readonly Animation _authored;

        internal WalkCycleContent()
        {
            _authored = new Animation { Flags = AnimationFlags.PosFrames };
            for (int frame = 0; frame < 4; frame++)
            {
                var partFrame = new AnimationFrame(1);
                partFrame.Frames.Add(new Frame
                {
                    Origin = Vector3.Zero,
                    Orientation = Quaternion.Identity,
                });
                _authored.PartFrames.Add(partFrame);
                _authored.PosFrames.Add(new Frame
                {
                    Origin = new Vector3(AuthoredMetresPerFrame, 0f, 0f),
                    Orientation = Quaternion.Identity,
                });
            }
            AnimationLoader = new SingleAnimation(AnimationId, _authored);
        }

        public IAnimationLoader AnimationLoader { get; }

        public MotionTable? TryGetMotionTable(uint motionTableId) => null;

        public Setup? TryGetSetup(uint setupId)
        {
            if (setupId != SetupId)
                return null;
            var setup = new Setup();
            setup.Parts.Add(0x0100AA01u);
            setup.DefaultScale.Add(Vector3.One);
            setup.DefaultAnimation =
                (QualifiedDataId<Animation>)AnimationId;
            return setup;
        }

        private sealed class SingleAnimation(uint id, Animation animation)
            : IAnimationLoader
        {
            public Animation? LoadAnimation(uint requested) =>
                requested == id ? animation : null;
        }
    }

    /// <summary>
    /// One authored shape for the creature, and nothing else. A body with no
    /// shape to hand is measured as a point, which changes where a sweep puts
    /// it, so both arms are given the same one.
    /// </summary>
    internal sealed class OneAuthoredShape : IPreparedCollisionSource
    {
        private static readonly FlatSetupCollision Shape = new(
            ImmutableArray<FlatCollisionCylinder>.Empty,
            ImmutableArray<FlatCollisionSphere>.Empty,
            height: 1.835f,
            radius: 0.48f,
            stepUpHeight: 0.4f,
            stepDownHeight: 0.4f);

        public PreparedAssetPresence ProbeCollision(
            PakAssetType type,
            uint sourceFileId) =>
            sourceFileId == SetupId
                ? PreparedAssetPresence.Available
                : PreparedAssetPresence.Missing;

        public PreparedCollisionReadResult<FlatSetupCollision>
            ReadSetupCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            sourceFileId == SetupId
                ? PreparedCollisionReadResult<FlatSetupCollision>.Loaded(Shape)
                : PreparedCollisionReadResult<FlatSetupCollision>.Missing;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset>
            ReadGfxObjCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatGfxObjCollisionAsset>.Missing;

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
                .Missing;

        public PreparedCollisionReadResult<FlatEnvCellTopology>
            ReadEnvCellTopology(
                uint sourceFileId,
                CancellationToken cancellationToken = default) =>
            PreparedCollisionReadResult<FlatEnvCellTopology>.Missing;

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }
}
