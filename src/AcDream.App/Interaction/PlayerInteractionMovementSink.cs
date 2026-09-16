using AcDream.App.Input;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;

namespace AcDream.App.Interaction;

internal interface IPlayerInteractionMovementSink
{
    bool BeginApproach(
        InteractionApproach approach,
        Action<PlayerApproachToken>? armAfterCancel = null);

    /// <summary>
    /// The local player's current move-to's consecutive per-tick
    /// progress-failure count (MoveToManager.FailProgressCount), or null
    /// when there is no active move-to to read. Resets to 0 the instant
    /// the move makes progress again, so a caller polling this to decide
    /// whether to give up on a stalled approach never mistakes a slow but
    /// still-advancing walk for a stuck one.
    /// </summary>
    uint? CurrentApproachFailProgressCount();

    /// <summary>
    /// Cancels the local player's current move-to outright (as
    /// WeenieError.ActionCancelled), the same call a new click's
    /// supersede-the-prior-approach path makes. Used when this host gives
    /// up on an approach that never naturally completed or cancelled, so
    /// the player stops walking into whatever is blocking it instead of
    /// silently continuing to try.
    /// </summary>
    void CancelApproach();
}

internal sealed class PlayerInteractionMovementSink(
    Func<PlayerMovementController?> player,
    IPlayerApproachTokenSource approachTokens)
    : IPlayerInteractionMovementSink
{
    private readonly Func<PlayerMovementController?> _player = player
        ?? throw new ArgumentNullException(nameof(player));
    private readonly IPlayerApproachTokenSource _approachTokens = approachTokens
        ?? throw new ArgumentNullException(nameof(approachTokens));

    public bool BeginApproach(
        InteractionApproach approach,
        Action<PlayerApproachToken>? armAfterCancel = null)
    {
        PlayerMovementController? controller = _player();
        if (controller?.MoveTo is null)
            return false;

        var parameters = new MovementParameters
        {
            DistanceToObject = approach.UseRadius,
            CanCharge = approach.CanCharge,
        };
        var movement = new MovementStruct
        {
            ObjectId = approach.Target.ServerGuid,
            TopLevelId = approach.Target.ServerGuid,
            Pos = new Position(
                approach.Player.CellId,
                approach.Target.Entity.Position,
                System.Numerics.Quaternion.Identity),
            Params = parameters,
            Type = approach.IsCloseRange
                ? MovementType.TurnToObject
                : MovementType.MoveToObject,
            Radius = approach.TargetRadius,
            Height = approach.TargetHeight,
        };

        controller.Movement.CancelMoveTo(WeenieError.ActionCancelled);
        if (!_approachTokens.TryBeginApproach(out PlayerApproachToken token))
            return false;
        armAfterCancel?.Invoke(token);

        controller.SetLastMoveWasAutonomous(false);
        return controller.Movement.PerformMovement(movement) == WeenieError.None;
    }

    public uint? CurrentApproachFailProgressCount()
        => _player()?.MoveTo?.FailProgressCount;

    public void CancelApproach()
        => _player()?.Movement.CancelMoveTo(WeenieError.ActionCancelled);
}
