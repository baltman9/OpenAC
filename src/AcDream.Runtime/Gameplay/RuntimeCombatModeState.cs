using AcDream.Core.Combat;
using AcDream.Core.Items;

namespace AcDream.Runtime.Gameplay;

public enum RuntimeCombatModeRequestStatus
{
    Inactive,
    Rejected,
    Sent,

    /// <summary>
    /// Accepted and parked: the body is still finishing a motion, and the
    /// change is sent on the first frame it is ready. The mode does not
    /// change until then.
    /// </summary>
    Deferred,
}

/// <summary>
/// What the mode change asks the body and the session before it goes out.
/// Bound by the runtime once the movement and transit owners exist.
/// </summary>
public interface IRuntimeCombatModeReadiness
{
    /// <summary>Whether the body is in position for the mode; strict, as a mode change asks it.</summary>
    bool IsInReadyPosition(CombatMode mode);

    /// <summary>Whether a teleport is under way, during which no mode change is accepted.</summary>
    bool IsTeleportInProgress { get; }
}

public readonly record struct RuntimeCombatModeRequestResult(
    RuntimeCombatModeRequestStatus Status,
    CombatMode Mode,
    string? Notice = null);

public interface IRuntimeCombatModeOperations
{
    bool IsInWorld { get; }
    IReadOnlyList<ClientObject> GetOrderedEquipment();
    void NotifyExplicitCombatModeRequest();
    void SendChangeCombatMode(CombatMode mode);
}

public sealed class RuntimeCombatModeState
{
    private readonly CombatState _combat;
    private readonly IRuntimeCombatModeOperations _operations;
    private IRuntimeCombatModeReadiness? _readiness;
    private CombatMode? _pendingMode;
    private long _qualifiedSelfMotionRevision;
    private double _qualifiedSelfMotionAt;

    public RuntimeCombatModeState(
        CombatState combat,
        IRuntimeCombatModeOperations operations)
    {
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _operations = operations
            ?? throw new ArgumentNullException(nameof(operations));
    }

    /// <summary>Gives the mode change the body and the session to ask. Without it, a change goes out at once.</summary>
    public void BindReadiness(IRuntimeCombatModeReadiness readiness) =>
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));

    /// <summary>The mode change parked until the body is ready, or null.</summary>
    public CombatMode? PendingMode => _pendingMode;

    /// <summary>
    /// Once a frame: sends a parked mode change the moment the body is in
    /// position for it. Nothing happens with nothing parked.
    /// </summary>
    public void ApplyPendingMode()
    {
        if (_pendingMode is not { } mode)
            return;
        if (!_operations.IsInWorld)
        {
            _pendingMode = null;
            return;
        }
        if (_readiness is { } readiness && !readiness.IsInReadyPosition(mode))
            return;
        _pendingMode = null;
        if (_combat.CurrentMode == mode)
            return;
        _operations.SendChangeCombatMode(mode);
        _combat.SetCombatMode(mode);
    }

    /// <summary>
    /// Sends the change now, or parks it while the body is still finishing a
    /// motion. A change parked earlier is replaced by this one.
    /// </summary>
    private RuntimeCombatModeRequestResult SendOrPark(CombatMode mode)
    {
        if (_readiness is { } readiness && !readiness.IsInReadyPosition(mode))
        {
            _pendingMode = mode;
            return new RuntimeCombatModeRequestResult(
                RuntimeCombatModeRequestStatus.Deferred,
                _combat.CurrentMode);
        }
        _pendingMode = null;
        _operations.SendChangeCombatMode(mode);
        _combat.SetCombatMode(mode);
        return new RuntimeCombatModeRequestResult(
            RuntimeCombatModeRequestStatus.Sent,
            mode);
    }

    private RuntimeCombatModeRequestResult? RefuseDuringTeleport()
    {
        if (_readiness is not { IsTeleportInProgress: true })
            return null;
        return new RuntimeCombatModeRequestResult(
            RuntimeCombatModeRequestStatus.Rejected,
            _combat.CurrentMode,
            "You can't change combat mode while teleporting.");
    }

    public long QualifiedSelfMotionRevision => _qualifiedSelfMotionRevision;

    public double QualifiedSelfMotionAgeSeconds(double now) =>
        _qualifiedSelfMotionRevision == 0
            ? 0d
            : Math.Max(0d, now - _qualifiedSelfMotionAt);

    public void RecordQualifiedSelfMotion(double now)
    {
        _qualifiedSelfMotionAt = now;
        _qualifiedSelfMotionRevision++;
    }

    public RuntimeCombatModeRequestResult Toggle()
    {
        if (!_operations.IsInWorld)
        {
            return new RuntimeCombatModeRequestResult(
                RuntimeCombatModeRequestStatus.Inactive,
                _combat.CurrentMode);
        }

        // Every explicit user request supersedes auto-wield settlement,
        // including a request GetDefaultCombatMode later rejects.
        _operations.NotifyExplicitCombatModeRequest();

        CombatMode currentMode = _combat.CurrentMode;
        CombatMode nextMode;
        if (currentMode != CombatMode.NonCombat)
        {
            nextMode = CombatMode.NonCombat;
        }
        else
        {
            DefaultCombatModeDecision decision =
                CombatInputPlanner.GetDefaultCombatModeDecision(
                    _operations.GetOrderedEquipment());
            if (decision.IncompatibleHeldItem is { } held)
            {
                string notice =
                    $"You can't enter combat mode while wielding the {held.GetAppropriateName()}";
                return new RuntimeCombatModeRequestResult(
                    RuntimeCombatModeRequestStatus.Rejected,
                    currentMode,
                    notice);
            }

            nextMode = CombatInputPlanner.ToggleMode(
                currentMode,
                decision.Mode);
        }

        if (RefuseDuringTeleport() is { } teleporting)
            return teleporting;
        return SendOrPark(nextMode);
    }

    public RuntimeCombatModeRequestResult Request(CombatMode mode)
    {
        if (!_operations.IsInWorld)
        {
            return new RuntimeCombatModeRequestResult(
                RuntimeCombatModeRequestStatus.Inactive,
                _combat.CurrentMode);
        }
        if (mode is not (CombatMode.NonCombat
            or CombatMode.Melee
            or CombatMode.Missile
            or CombatMode.Magic))
        {
            return new RuntimeCombatModeRequestResult(
                RuntimeCombatModeRequestStatus.Rejected,
                _combat.CurrentMode,
                "Invalid combat mode.");
        }
        if (_combat.CurrentMode == mode)
        {
            return new RuntimeCombatModeRequestResult(
                RuntimeCombatModeRequestStatus.Sent,
                mode);
        }

        _operations.NotifyExplicitCombatModeRequest();
        if (RefuseDuringTeleport() is { } teleporting)
            return teleporting;
        return SendOrPark(mode);
    }
}
