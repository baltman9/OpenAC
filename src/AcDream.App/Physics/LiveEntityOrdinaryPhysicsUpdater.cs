using System.Collections.Immutable;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Core.World;
using AcDream.Runtime.Physics;
using DatReaderWriter.Types;

namespace AcDream.App.Physics;

internal sealed class LiveEntityOrdinaryPhysicsUpdater
{
    private readonly RuntimeOrdinaryPhysicsUpdater _runtime;
    private readonly Func<uint, WorldEntity, (float Radius, float Height)>
        _getSetupCylinder;
    private readonly Func<uint, WorldEntity,
            (ImmutableArray<FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)>
        _getSetupMoverShape;
    private readonly Func<uint, ObjectInfoState> _getMoverPvpState;

    public LiveEntityOrdinaryPhysicsUpdater(
        RuntimePhysicsState physics,
        Func<uint, WorldEntity, (float Radius, float Height)> getSetupCylinder,
        Func<uint, WorldEntity,
                (ImmutableArray<FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)>
            getSetupMoverShape,
        Func<uint, ObjectInfoState>? getMoverPvpState = null)
    {
        _runtime = new RuntimeOrdinaryPhysicsUpdater(
            physics ?? throw new ArgumentNullException(nameof(physics)));
        _getSetupCylinder = getSetupCylinder
            ?? throw new ArgumentNullException(nameof(getSetupCylinder));
        _getSetupMoverShape = getSetupMoverShape
            ?? throw new ArgumentNullException(nameof(getSetupMoverShape));
        _getMoverPvpState = getMoverPvpState ?? (static _ => ObjectInfoState.None);
    }

    public bool Tick(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        Frame rootFrame,
        float objectScale,
        float quantum,
        int liveCenterX,
        int liveCenterY,
        ulong objectClockEpoch,
        AnimationSequencer? sequencer,
        Action<uint, AnimationSequencer> captureAnimationHooks)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(rootFrame);
        ArgumentNullException.ThrowIfNull(captureAnimationHooks);
        if (record.PhysicsBody is not { } body)
            return false;
        var (radius, height) = _getSetupCylinder(record.ServerGuid, entity);
        var shape = _getSetupMoverShape(record.ServerGuid, entity);
        // The two callbacks below are aimed at one carried state object
        // instead of closing over the locals: this runs for every live entity
        // on every frame, and a capture would allocate two delegates and a
        // display class each time. Entities are ticked one at a time and the
        // callbacks are only invoked inside this call.
        TickCallbacks callbacks = _callbacks;
        callbacks.Aim(runtime, record, entity, body, objectClockEpoch);

        if (!_runtime.TryBegin(
                record.Canonical,
                rootFrame,
                objectScale,
                quantum,
                radius,
                height,
                objectClockEpoch,
                sequencer,
                captureAnimationHooks,
                callbacks.ExternalOwnerValid,
                out RuntimeOrdinaryPhysicsCommit commit,
                out RuntimeOrdinaryPhysicsTicket ticket,
                sphereList: shape.Spheres,
                sphereScale: shape.Scale,
                stepUpHeight: shape.StepUpHeight,
                stepDownHeight: shape.StepDownHeight,
                moverPvpState: _getMoverPvpState(record.ServerGuid)))
        {
            callbacks.Release();
            return false;
        }

        bool completed = _runtime.Complete(
            commit,
            ticket,
            liveCenterX,
            liveCenterY,
            callbacks.Commit);
        callbacks.Release();
        return completed;
    }

    private readonly TickCallbacks _callbacks = new();

    /// <summary>
    /// Holds what the per-entity physics callbacks read, so the callbacks
    /// themselves are created once for the updater.
    /// </summary>
    private sealed class TickCallbacks
    {
        private LiveEntityRuntime? _runtime;
        private LiveEntityRecord? _record;
        private WorldEntity? _entity;
        private PhysicsBody? _body;
        private ulong _objectClockEpoch;

        public TickCallbacks()
        {
            ExternalOwnerValid = IsOwnerValid;
            Commit = ApplySnapshot;
        }

        public Func<bool> ExternalOwnerValid { get; }

        public Func<RuntimePhysicsFrameSnapshot, bool> Commit { get; }

        public void Aim(
            LiveEntityRuntime runtime,
            LiveEntityRecord record,
            WorldEntity entity,
            PhysicsBody body,
            ulong objectClockEpoch)
        {
            _runtime = runtime;
            _record = record;
            _entity = entity;
            _body = body;
            _objectClockEpoch = objectClockEpoch;
        }

        public void Release()
        {
            _runtime = null;
            _record = null;
            _entity = null;
            _body = null;
            _objectClockEpoch = 0ul;
        }

        private bool IsOwnerValid()
        {
            if (_runtime is null || _record is null || _entity is null || _body is null)
                throw new InvalidOperationException("no entity is being ticked");
            return IsCurrent(_runtime, _record, _entity, _body, _objectClockEpoch);
        }

        private bool ApplySnapshot(RuntimePhysicsFrameSnapshot snapshot)
        {
            if (!IsOwnerValid())
                return false;

            WorldEntity entity = _entity!;
            entity.SetPosition(snapshot.Position);
            entity.Rotation = snapshot.Orientation;
            entity.ParentCellId = snapshot.FullCellId;
            return IsOwnerValid();
        }
    }

    private static bool IsCurrent(
        LiveEntityRuntime runtime,
        LiveEntityRecord record,
        WorldEntity entity,
        PhysicsBody body,
        ulong objectClockEpoch) =>
        runtime.IsCurrentSpatialRootObject(record)
        && record.ObjectClockEpoch == objectClockEpoch
        && ReferenceEquals(record.WorldEntity, entity)
        && ReferenceEquals(record.PhysicsBody, body)
        && record.RemoteMotionRuntime is null
        && record.ProjectileRuntime is null;
}
