using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Input;
using AcDream.App.Physics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Vfx;
using AcDream.App.Streaming;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The client with a window, carrying another creature's body the way it
/// really does: its own per-frame scheduler over its own drawn-entity runtime,
/// with the real physics updaters underneath. Nothing is drawn -- there is no
/// graphics card -- so the pose work ends at the part poses the scheduler
/// hands back, which is as far as this path goes without one.
/// </summary>
internal sealed class WindowedBodyArm : RemoteBodyArm
{
    private readonly GpuWorldState _spatial = new();
    private readonly LiveEntityRuntime _live;
    private readonly LiveEntityMotionRuntimeController _motion;
    private readonly RuntimeRemoteArming _arming;
    private readonly LiveWorldOriginState _origin = new();
    private readonly EntityEffectPoseRegistry _poses = new();
    private LiveEntityRecord? _record;
    private WorldEntity? _entity;
    private LiveEntityAnimationState? _animation;
    private LiveEntityAnimationScheduler? _scheduler;

    internal WindowedBodyArm()
        : base(ParityHost.Windowed)
    {
        _spatial.AddLandblock(new LoadedLandblock(
            Landblock,
            new DatReaderWriter.DBObjs.LandBlock(),
            Array.Empty<WorldEntity>()));
        _ = _origin.TryInitialize(CenterX, CenterY);
        _live = new LiveEntityRuntime(
            _spatial,
            new DelegateLiveEntityResourceLifecycle(_ => { }, _ => { }),
            Lifetime);
        _motion = new LiveEntityMotionRuntimeController(
            _live,
            static () => null,
            new SelectionState(),
            _origin);
        _arming = RuntimeRemoteArming.Create(
            Lifetime,
            new AcDream.Runtime.GameRuntimeClock(),
            new OneAuthoredShape(),
            new AnyDestination(),
            _motion.HostFacts);
        _motion.BindArming(_arming);
    }

    internal override RuntimeRemoteArming Arming => _arming;

    internal override RuntimeEntityRecord Record =>
        (_record ?? throw new InvalidOperationException(
            "The creature has not arrived on this client.")).Canonical;

    /// <summary>
    /// The clock answer this client has always given: off the projection that
    /// draws the body.
    /// </summary>
    protected override bool RootClockAdvances =>
        _live.GetRootObjectClockDisposition(Creature)
            is RetailObjectClockDisposition.Advance;

    internal override void CreatureArrives()
    {
        AcDream.App.World.LiveEntityRegistrationResult registration =
            _live.RegisterLiveEntity(Spawn());
        RuntimeEntityRecord canonical = registration.Canonical
            ?? throw new InvalidOperationException(
                "The creature's creation description was refused.");
        _ = _live.MaterializeLiveEntity(
            canonical,
            Cell,
            id => new WorldEntity
            {
                Id = id,
                ServerGuid = Creature,
                SourceGfxObjOrSetupId = SetupId,
                Position = Start,
                Rotation = Quaternion.Identity,
                MeshRefs = Array.Empty<MeshRef>(),
                ParentCellId = Cell,
            },
            LiveEntityProjectionKind.World,
            initializeProjection: null,
            out LiveEntityRecord? projected);
        _record = projected
            ?? throw new InvalidOperationException(
                "The creature was never drawn.");
        Assert.True(Lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            canonical.Snapshot,
            replaceGeneration: false));
        _entity = Assert.IsType<WorldEntity>(_record.WorldEntity);
        _record.HasPartArray = true;
        _poses.PublishMeshRefs(_entity);

        _animation = new LiveEntityAnimationState
        {
            Entity = _entity,
            Setup = new DatReaderWriter.DBObjs.Setup(),
            Animation = null!,
            LowFrame = 0,
            HighFrame = 0,
            Framerate = 0f,
            PartTemplate = Array.Empty<LiveAnimationPartTemplate>(),
            PartAvailability = Array.Empty<bool>(),
            CurrFrame = 0,
            // Out of the same one builder both clients read their bodies from.
            Simulation = Lifetime.Physics.MotionStates!.CreateFromSpawn(
                Spawn(),
                Lifetime.Physics.EntityObjectScale(Record)),
        };
        _live.SetAnimationRuntime(Creature, _animation);

        _scheduler = new LiveEntityAnimationScheduler(
            _live,
            new LocalPlayerIdentityState { ServerGuid = Player },
            new RuntimeRemoteBodyOwner(_live.Physics),
            new LiveEntityOrdinaryPhysicsUpdater(
                _live.Physics,
                (id, _) => _live.Physics.EntityBodyShape(id) ?? (0f, 0f),
                (id, _) => _live.Physics.EntityMoverShape(id)),
            new ProjectileController(_live),
            new PoseRegistryPublisher(_poses),
            new NoHooks());
    }

    protected override void OnBodyGiven(RemoteMotion remote) =>
        _live.SetRemoteMotionRuntime(Creature, remote);

    internal override void Tick(float elapsedSeconds)
    {
        if (_scheduler is null || _entity is null)
            return;
        _ = _scheduler.Tick(
            elapsedSeconds,
            PlayerPosition,
            localHiddenPartPoseDirty: false,
            CenterX,
            CenterY);
    }

    private sealed class PoseRegistryPublisher(EntityEffectPoseRegistry poses)
        : IEntityRootPosePublisher
    {
        public void UpdateRoot(WorldEntity entity) => poses.UpdateRoot(entity);
    }

    private sealed class NoHooks : IAnimationHookCaptureSink
    {
        public void Capture(uint ownerLocalId, AnimationSequencer sequencer)
        {
        }
    }
}

/// <summary>
/// The client with no window, carrying another creature's body the way it
/// really does: the shared drive, from the same place in the frame its own
/// session host calls it.
/// </summary>
internal sealed class WindowlessBodyArm : RemoteBodyArm
{
    private readonly RuntimeRemoteArming _arming;
    private readonly RuntimeRemoteBodyDrive _drive;
    private RuntimeEntityRecord? _record;

    internal WindowlessBodyArm()
        : base(ParityHost.Windowless)
    {
        _arming = RuntimeRemoteArming.Create(
            Lifetime,
            new AcDream.Runtime.GameRuntimeClock(),
            new OneAuthoredShape(),
            new AnyDestination());
        _drive = new RuntimeRemoteBodyDrive(
            Lifetime,
            static () => Player,
            () => PlayerPosition);
    }

    internal override RuntimeRemoteArming Arming => _arming;

    internal override RuntimeEntityRecord Record =>
        _record ?? throw new InvalidOperationException(
            "The creature has not arrived on this client.");

    /// <summary>
    /// The clock answer this client gives: off the record, because there is
    /// no projection to ask.
    /// </summary>
    protected override bool RootClockAdvances =>
        RuntimeRemoteBodyDisposition.RootClock(Lifetime.Physics, Record)
            is RetailObjectClockDisposition.Advance;

    internal override void CreatureArrives()
    {
        RuntimeEntityRecord canonical = Lifetime.RegisterEntity(Spawn())
            .Canonical
            ?? throw new InvalidOperationException(
                "The creature's creation description was refused.");
        Assert.True(Lifetime.ApplyAcceptedSpawn(
            canonical,
            canonical.CreateIntegrationVersion,
            canonical.Snapshot,
            replaceGeneration: false));
        canonical.HasPartArray = true;
        _record = canonical;
    }

    internal override void Tick(float elapsedSeconds) =>
        _drive.Tick(elapsedSeconds);
}
