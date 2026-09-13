using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Physics;

namespace AcDream.Runtime.Session;

/// <summary>
/// A movement the server drives at the local character: it names the thing to
/// walk to, how close to stop, and how fast, and the character walks there on
/// its own.
/// <para>
/// This is not a nicety. Ask the server to use something that is not already
/// within its reach and it does not refuse: it sends the character to it and
/// waits. A character that never walks is told, fifteen seconds later, that
/// the use is "done" — with no error, and nothing done. Every use issued from
/// beyond that reach fails that way, silently, and a corpse the fight left a
/// couple of metres off is the ordinary case.
/// </para>
/// <para>
/// A host with a window has always obeyed these orders; a host without one has
/// the same movement machinery and the same obligation. When the thing named
/// has no body of its own to follow, the walk falls back to the place the
/// order names, which is what the original client does in the same spot.
/// </para>
/// </summary>
internal static class RuntimeServerControlledLocalMovement
{
    private const byte MoveToObjectMovementType = 6;
    private const byte TurnToObjectMovementType = 8;

    /// <summary>
    /// Applies one server-driven move-to or turn-to aimed at the local
    /// character. True when a movement was started.
    /// </summary>
    public static bool TryApply(
        GameRuntime runtime,
        in WorldSession.EntityMotionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.MovementOwner.Controller is not { } controller)
            return false;

        MovementManager movement = controller.Movement;
        if (movement.MoveTo is null)
            return false;

        RuntimePhysicsState physics = runtime.EntityObjects.Physics;
        if (!TryResolve(
                update,
                targetBodyRadius: guid =>
                    physics.ResolveObjectTableHost(guid)?.Radius,
                worldFrameOffset: cellId =>
                    physics.TryGetWorldFrameOffset(
                        cellId,
                        out float offsetX,
                        out float offsetY)
                        ? (offsetX, offsetY)
                        : null,
                localCellId: controller.CellId,
                out MovementStruct request,
                out float? runRate))
        {
            return false;
        }

        if (runRate is { } rate)
            movement.Minterp.MyRunRate = rate;
        _ = movement.PerformMovement(request);
        return true;
    }

    /// <summary>
    /// What one inbound movement asks the local character to do, or nothing
    /// when it asks for nothing this host owes an answer to.
    /// </summary>
    /// <param name="targetBodyRadius">
    /// The radius of the named thing's body, or null when it has none to
    /// follow.
    /// </param>
    /// <param name="worldFrameOffset">
    /// How far the named cell's landblock sits from the frame the character's
    /// own body is measured in, or null when that frame is not established.
    /// </param>
    internal static bool TryResolve(
        in WorldSession.EntityMotionUpdate update,
        Func<uint, float?> targetBodyRadius,
        Func<uint, (float X, float Y)?> worldFrameOffset,
        uint localCellId,
        out MovementStruct request,
        out float? runRate)
    {
        ArgumentNullException.ThrowIfNull(targetBodyRadius);
        ArgumentNullException.ThrowIfNull(worldFrameOffset);
        request = default;
        runRate = null;

        // The server echoes the character's own movement back with the
        // autonomous flag set. Only a movement the server itself decided on
        // is one to obey.
        if (update.IsAutonomous)
            return false;

        CreateObject.ServerMotionState state = update.MotionState;
        if (state.IsServerControlledMoveTo)
        {
            return TryResolveMoveTo(
                state,
                targetBodyRadius,
                worldFrameOffset,
                localCellId,
                ref request,
                ref runRate);
        }
        return state.IsServerControlledTurnTo
            && TryResolveTurnTo(state, targetBodyRadius, ref request);
    }

    private static bool TryResolveMoveTo(
        in CreateObject.ServerMotionState state,
        Func<uint, float?> targetBodyRadius,
        Func<uint, (float X, float Y)?> worldFrameOffset,
        uint localCellId,
        ref MovementStruct request,
        ref float? runRate)
    {
        if (state.MoveToPath is not { } path)
            return false;

        runRate = state.MoveToRunRate;
        request.Params = MovementParameters.FromWire(
            path.Bitfield,
            path.DistanceToObject,
            path.MinDistance,
            path.FailDistance,
            state.MoveToSpeed ?? 1f,
            path.WalkRunThreshold,
            path.DesiredHeading);

        if (state.MovementType == MoveToObjectMovementType
            && path.TargetGuid is { } targetGuid
            && targetBodyRadius(targetGuid) is { } radius)
        {
            // Following the thing itself: the walk tracks where it is, not
            // where it was when the order was written.
            request.Type = MovementType.MoveToObject;
            request.ObjectId = targetGuid;
            request.TopLevelId = targetGuid;
            request.Radius = radius;
            return true;
        }

        if (worldFrameOffset(path.OriginCellId) is not { } offset)
            return false;

        request.Type = MovementType.MoveToPosition;
        request.Pos = new Position(
            localCellId,
            new Vector3(
                path.OriginX + offset.X,
                path.OriginY + offset.Y,
                path.OriginZ),
            Quaternion.Identity);
        return true;
    }

    private static bool TryResolveTurnTo(
        in CreateObject.ServerMotionState state,
        Func<uint, float?> targetBodyRadius,
        ref MovementStruct request)
    {
        if (state.TurnToPath is not { } path)
            return false;

        MovementParameters parameters = MovementParameters.FromWireTurnTo(
            path.Bitfield,
            path.Speed,
            path.DesiredHeading);
        request.Params = parameters;

        if (state.MovementType == TurnToObjectMovementType
            && path.TargetGuid is { } targetGuid
            && targetBodyRadius(targetGuid) is not null)
        {
            request.Type = MovementType.TurnToObject;
            request.ObjectId = targetGuid;
            request.TopLevelId = targetGuid;
            return true;
        }

        request.Type = MovementType.TurnToHeading;
        if (state.MovementType == TurnToObjectMovementType
            && path.WireHeading is { } wireHeading)
        {
            parameters.DesiredHeading = wireHeading;
        }
        return true;
    }
}
