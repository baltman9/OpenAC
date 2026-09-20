using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Interaction;
using AcDream.App.Rendering;
using AcDream.App.World;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.World;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Physics;

namespace AcDream.App.Physics;

/// <summary>
/// What this client — the one with a window — knows about another creature's
/// body when the server's word about it arrives, and how it hands that to the
/// shared arming.
/// </summary>
/// <remarks>
/// The arming itself is not here any more: a body is armed the same way on
/// every client, so one owner does it. What IS here is the short list of
/// things only a client with a drawn world can answer — which bodies it is
/// drawing, where it has them, and the centre it measures a landblock-local
/// place from — handed over as the facts the shared arming asks for.
/// </remarks>
internal sealed class LiveEntityMotionRuntimeController
    : ILiveEntityMotionRuntimeBindings
{
    private readonly LiveEntityRuntime _liveEntities;
    private readonly Func<SelectionInteractionController?> _selectionInteractions;
    private readonly SelectionState _selection;
    private readonly LiveWorldOriginState _origin;

    private RuntimeRemoteArming? _arming;

    public LiveEntityMotionRuntimeController(
        LiveEntityRuntime liveEntities,
        Func<SelectionInteractionController?> selectionInteractions,
        SelectionState selection,
        LiveWorldOriginState origin)
    {
        _liveEntities = liveEntities ?? throw new ArgumentNullException(nameof(liveEntities));
        _selectionInteractions = selectionInteractions ?? throw new ArgumentNullException(nameof(selectionInteractions));
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
    }

    /// <summary>
    /// What this client can tell the shared arming that the arming cannot work
    /// out for itself.
    /// </summary>
    /// <remarks>
    /// Each answer is the drawn world's, and each is the one the window has
    /// always used. "A body I am drawing" is a stronger test than "a live
    /// record": the projection that draws a body can be replaced while the
    /// record lives on, and a body whose drawn form has gone is not one to
    /// hang a physics host off.
    /// </remarks>
    public RuntimeRemoteArmingHostFacts HostFacts => new(
        DrawnBody: serverGuid =>
            _liveEntities.TryGetRecord(serverGuid, out LiveEntityRecord record)
                && record.WorldEntity is not null
                ? new RuntimeRemoteArmingDrawnBody(
                    record.Canonical,
                    () => record.WorldEntity?.Position,
                    () => _liveEntities.TryGetRecord(
                            serverGuid, out LiveEntityRecord current)
                        && ReferenceEquals(current, record))
                : null,
        WireOriginToWorld: (originCellId, x, y, z) =>
            AcDream.Core.Physics.Motion.MoveToMath.OriginToWorld(
                originCellId, x, y, z, _origin.CenterX, _origin.CenterY),
        InteractionTargetPosition: serverGuid =>
            _liveEntities.TryGetInteractionEligibleEntity(
                serverGuid, out WorldEntity target)
                ? target.Position
                : null);

    /// <summary>
    /// Hands this client's facts to the one arming, once per session. The
    /// arming is built where the collision catalogue and the service window
    /// are, which is later than this controller.
    /// </summary>
    public void BindArming(RuntimeRemoteArming arming)
    {
        ArgumentNullException.ThrowIfNull(arming);
        if (_arming is not null)
        {
            throw new InvalidOperationException(
                "The live-entity motion controller is already bound to a "
                + "remote arming.");
        }
        _arming = arming;
    }

    private RuntimeRemoteArming Arming =>
        _arming ?? throw new InvalidOperationException(
            "The live-entity motion controller has no remote arming bound; "
            + "the session composition must bind one before the first "
            + "inbound movement.");

    internal AcDream.Core.Physics.Motion.MotionTableDispatchSink? EnsureRemoteMotionBindings(
        RemoteMotion rm, LiveEntityAnimationState? ae, uint serverGuid) =>
        Arming.EnsureRemoteMotionBindings(rm, ae?.Sequencer, serverGuid);

    public AcDream.Core.Physics.Motion.IPhysicsObjHost? ResolvePhysicsHost(uint id)
    {
        if (_liveEntities is not { } liveEntities)
            return null;

        bool isActive = liveEntities.TryGetRecord(id, out LiveEntityRecord activeRecord);
        if (!isActive)
            return null;

        if (liveEntities.IsHidden(id))
        {
            return null;
        }
        if (liveEntities.TryGetPhysicsHost(id, out var existing))
            return existing;

        double NowSeconds() => (System.DateTime.UtcNow - System.DateTime.UnixEpoch).TotalSeconds;
        var minimal = EntityPhysicsHostComposition.CreateMinimal(
            activeRecord,
            ResolvePhysicsHost,
            NowSeconds);
        liveEntities.InstallPhysicsHost(activeRecord, minimal);
        return minimal;
    }

    /// <summary>
    /// How wide and how tall the thing with this id is, in metres.
    /// </summary>
    /// <remarks>
    /// Worked out in ONE place — the shared physics owner, from the authored
    /// shape and the scale the server gave this particular thing — so that a
    /// walk ordered at a creature measures the same gap whether or not there
    /// is a window. This stays as the seam the drawn-world callers already
    /// hold; the entity they pass no longer decides anything, because the id
    /// is enough to find the shape.
    /// </remarks>
    public (float Radius, float Height) GetSetupCylinder(
        uint serverGuid, AcDream.Core.World.WorldEntity entity)
        => _liveEntities.Physics.EntityBodyShape(serverGuid) ?? (0f, 0f);

    /// <summary>
    /// The shape this thing is swept as while it moves, from the same one
    /// place and the same authored shape as its girth and height.
    /// </summary>
    public (ImmutableArray<FlatCollisionSphere> Spheres, float Scale, float StepUpHeight, float StepDownHeight)
        GetSetupMoverShape(uint serverGuid, AcDream.Core.World.WorldEntity entity)
        => _liveEntities.Physics.EntityMoverShape(serverGuid);


    public void StickToObjectFromWire(
        AcDream.Core.Physics.Motion.IPhysicsObjHost? host,
        uint targetGuid) =>
        Arming.StickToObjectFromWire(host, targetGuid);

    public void ClearTargetForHiddenEntity(uint serverGuid)
    {
        // Letting the selection go is the runtime's, for every client.
        if (_selectionInteractions() is { } interactions)
            interactions.OnEntityHidden(serverGuid);

        if (_liveEntities?.TryGetPhysicsHost(serverGuid, out var hiddenHost) == true
            && hiddenHost is EntityPhysicsHost hiddenEntityHost)
            hiddenEntityHost.NotifyHidden();
    }

    public bool RouteServerMoveTo(
        AcDream.Core.Physics.Motion.MovementManager movement,
        uint cellId,
        AcDream.Core.Net.WorldSession.EntityMotionUpdate update) =>
        Arming.RouteServerMoveTo(movement, cellId, update);
}
