using AcDream.App.UI;
using AcDream.App.World;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.Ui;
using AcDream.Runtime.Gameplay;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Interaction;

/// <summary>
/// The plugin-facing outcome of a walk-then-use attempt on a world object.
/// </summary>
internal enum AutomationUseOutcome
{
    Started,
    Busy,
    NotUseable,
    NotInWorld,

    /// <summary>
    /// The outbound send itself was rejected by the transport layer (not
    /// a busy gate, not an unusable target) -- distinct from Busy so a
    /// caller does not read it as "try again shortly".
    /// </summary>
    Unavailable,
}

internal sealed class SelectionInteractionController
{
    /// <summary>
    /// How long an armed walk-then-use is allowed to wait for the local
    /// physics to report arrival before this host gives up on it. Retail's
    /// own MoveToObject has no such bound because it always resolves one
    /// way or another; this port's local movement can stop making progress
    /// against an obstruction (a closed door, a wall in the straight-line
    /// path) without ever calling MoveToComplete or MoveToCancelled, which
    /// left an armed Use waiting forever and wedged the one-request-at-a-
    /// time gate for the rest of the session -- this is
    /// an engineering safeguard against that missing termination signal,
    /// not a retail timing value.
    /// </summary>
    internal const long PendingUseArrivalTimeoutMs = 15_000;

    private readonly SelectionState _selection;
    private readonly IWorldSelectionQuery _query;
    private readonly ItemInteractionController _items;
    private readonly RuntimeInteractionTransactionState _transactions;
    private readonly IRuntimeInteractionTransport _transport;
    private readonly IPlayerInteractionMovementSink _movement;
    private readonly PlayerApproachCompletionState _approachCompletions;
    private readonly Action<string>? _toast;
    private readonly Func<uint, bool>? _splitStack;
    private readonly Func<IEnumerable<uint>> _fellowshipMembers;
    private readonly RuntimeCombatTargetState _combatTarget;
    private readonly Func<long> _nowMs;
    private long? _pendingUseArmedAtMs;

    public SelectionInteractionController(
        SelectionState selection,
        IWorldSelectionQuery query,
        ItemInteractionController items,
        IRuntimeInteractionTransport transport,
        IPlayerInteractionMovementSink movement,
        RuntimeCombatTargetState combatTarget,
        Action<string>? toast = null,
        PlayerApproachCompletionState? approachCompletions = null,
        Func<uint, bool>? splitStack = null,
        Func<IEnumerable<uint>>? fellowshipMembers = null,
        Func<long>? nowMs = null)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _transactions = _items.RuntimeTransactions;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _movement = movement ?? throw new ArgumentNullException(nameof(movement));
        _combatTarget = combatTarget
            ?? throw new ArgumentNullException(nameof(combatTarget));
        _toast = toast;
        _approachCompletions = approachCompletions
            ?? new PlayerApproachCompletionState();
        _splitStack = splitStack;
        _fellowshipMembers = fellowshipMembers ?? (() => Array.Empty<uint>());
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public bool HandleInputAction(InputAction action)
    {
        switch (action)
        {
            case InputAction.SelectionSelf:
                SelectSelf();
                return true;
            case InputAction.SelectionPlaceInInventory:
                PlaceSelectionInBackpack(mainPack: false);
                return true;
            case InputAction.SelectionPlaceInMainPack:
                PlaceSelectionInBackpack(mainPack: true);
                return true;
            case InputAction.SelectionSplitStack:
                if (_selection.SelectedObjectId is { } stack)
                    _splitStack?.Invoke(stack);
                return true;
            case InputAction.SelectionClosestCompassItem:
                SelectRetailTarget(RetailSelectionKind.CompassItem, RetailSelectionDirection.Closest);
                return true;
            case InputAction.SelectionPreviousCompassItem:
                SelectRetailTarget(RetailSelectionKind.CompassItem, RetailSelectionDirection.Previous);
                return true;
            case InputAction.SelectionNextCompassItem:
                SelectRetailTarget(RetailSelectionKind.CompassItem, RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionClosestItem:
                SelectRetailTarget(
                    RetailSelectionKind.Item,
                    RetailSelectionDirection.Closest,
                    excludeOwnedByPlayer: true);
                return true;
            case InputAction.SelectionPreviousItem:
                SelectRetailTarget(RetailSelectionKind.Item, RetailSelectionDirection.Previous);
                return true;
            case InputAction.SelectionNextItem:
                SelectRetailTarget(RetailSelectionKind.Item, RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionClosestMonster:
                SelectRetailTarget(
                    RetailSelectionKind.Monster,
                    RetailSelectionDirection.Closest,
                    showToast: true);
                return true;
            case InputAction.SelectionPreviousMonster:
                SelectRetailTarget(RetailSelectionKind.Monster, RetailSelectionDirection.Previous);
                return true;
            case InputAction.SelectionNextMonster:
                SelectRetailTarget(RetailSelectionKind.Monster, RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionLastAttacker:
                if (_query.FindLastAttacker() is { } attacker)
                    _selection.Select(attacker, SelectionChangeSource.Keyboard);
                return true;
            case InputAction.SelectionClosestPlayer:
                SelectRetailTarget(RetailSelectionKind.Player, RetailSelectionDirection.Closest);
                return true;
            case InputAction.SelectionPreviousPlayer:
                SelectRetailTarget(RetailSelectionKind.Player, RetailSelectionDirection.Previous);
                return true;
            case InputAction.SelectionNextPlayer:
                SelectRetailTarget(RetailSelectionKind.Player, RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionPreviousFellow:
                SelectFellow(previous: true);
                return true;
            case InputAction.SelectionNextFellow:
                SelectFellow(previous: false);
                return true;
            case InputAction.SelectionClosestUnopenedCorpse:
                SelectRetailTarget(RetailSelectionKind.UnopenedCorpse, RetailSelectionDirection.Closest);
                return true;
            case InputAction.SelectionNextUnopenedCorpse:
                SelectRetailTarget(RetailSelectionKind.UnopenedCorpse, RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionUseClosestUnopenedCorpse:
                SelectAndUseCorpse(RetailSelectionDirection.Closest);
                return true;
            case InputAction.SelectionUseNextUnopenedCorpse:
                SelectAndUseCorpse(RetailSelectionDirection.Next);
                return true;
            case InputAction.SelectionGiveToTarget:
                GiveSelectionToPreviousTarget();
                return true;
            case InputAction.SelectionDrop:
                DropSelection();
                return true;
            case InputAction.SelectionPreviousSelection:
                _selection.SelectPrevious();
                return true;
            case InputAction.SelectLeft:
                PickAndStoreSelection(useImmediately: false);
                return true;
            case InputAction.SelectRight:
                PickSelectAndExamine();
                return true;
            case InputAction.SelectDblLeft:
                PickAndStoreSelection(useImmediately: true);
                return true;
            case InputAction.SelectionExamine:
                _items.ExamineSelectedOrEnterMode(
                    _selection.SelectedObjectId ?? 0u);
                return true;
            case InputAction.UseSelected:
                UseCurrentSelection();
                return true;
            case InputAction.SelectionPickUp:
                if (_selection.SelectedObjectId is uint pickupTarget)
                {
                    EnqueueIdentityBound(
                        RuntimeQueuedInteractionKind.Pickup,
                        pickupTarget,
                        requireLiveEntity: true);
                }
                else
                {
                    _toast?.Invoke("Nothing selected");
                }
                return true;
            case InputAction.EscapeKey when _items.IsAnyTargetModeActive:
                _items.CancelTargetMode();
                return true;
            case InputAction.EscapeKey when _selection.SelectedObjectId is not null:
                // The player asked to drop the target, so say so before the
                // selection empties: automatic targeting would otherwise pick
                // the same creature straight back up and the press would only
                // make the target flicker.
                _combatTarget.NotifyTargetWillinglyLost();
                _selection.Clear(SelectionChangeSource.Keyboard);
                return true;
            default:
                return false;
        }
    }

    private void SelectSelf()
    {
        uint playerGuid = _query.PlayerGuid;
        if (playerGuid == 0u)
            return;
        if (_items.OfferPrimaryClick(playerGuid) is not ItemPrimaryClickResult.NotActive)
            return;
        _selection.Select(playerGuid, SelectionChangeSource.Keyboard);
    }

    private void PlaceSelectionInBackpack(bool mainPack)
    {
        if (_selection.SelectedObjectId is { } selected)
            _items.PlaceWorldItemInBackpack(selected, mainPack);
    }

    private void SelectRetailTarget(
        RetailSelectionKind kind,
        RetailSelectionDirection direction,
        bool excludeOwnedByPlayer = false,
        bool showToast = false)
    {
        uint? anchor = _selection.SelectedObjectId ?? _selection.PreviousObjectId;
        uint? target = _query.FindSelectionTarget(
            kind,
            direction,
            anchor,
            excludeOwnedByPlayer);
        if (target is { } guid)
        {
            _selection.Select(guid, SelectionChangeSource.Keyboard);
            if (showToast)
                _toast?.Invoke(_query.Describe(guid));
        }
    }

    private void SelectAndUseCorpse(RetailSelectionDirection direction)
    {
        SelectRetailTarget(RetailSelectionKind.UnopenedCorpse, direction);
        if (_selection.SelectedObjectId is { } corpse)
            EnqueueIdentityBound(
                RuntimeQueuedInteractionKind.Use,
                corpse,
                requireLiveEntity: false);
    }

    private void SelectFellow(bool previous)
    {
        uint[] fellows = _fellowshipMembers()
            .Where(static guid => guid != 0u)
            .Distinct()
            .ToArray();
        if (fellows.Length == 0)
            return;

        int current = _selection.SelectedObjectId is { } selected
            ? Array.IndexOf(fellows, selected)
            : -1;
        int next = previous
            ? (current > 0 ? current - 1 : fellows.Length - 1)
            : (current >= 0 && current + 1 < fellows.Length ? current + 1 : 0);
        _selection.Select(fellows[next], SelectionChangeSource.Keyboard);
    }

    private void GiveSelectionToPreviousTarget()
    {
        if (_selection.SelectedObjectId is not { } selected
            || _selection.PreviousObjectId is not { } target
            || selected == target
            || !_query.IsCreature(target))
        {
            _toast?.Invoke(
                "You must select a creature or a character to give that to.\n");
            return;
        }

        if (_items.PlaceSelectedIn3D(selected, target))
            _selection.Select(target, SelectionChangeSource.Keyboard);
    }

    private void DropSelection()
    {
        if (_selection.SelectedObjectId is not { } selected)
            return;
        if (!_items.IsOwnedByPlayer(selected))
        {
            _toast?.Invoke("You must pick that up first");
            return;
        }
        _items.PlaceSelectedIn3D(selected, targetGuid: 0u);
    }

    public uint? PickAtCursor(bool includeSelf)
        => _query.PickAtCursor(includeSelf);

    public void PlaceDraggedItem(ItemDragPayload payload, float mouseX, float mouseY)
    {
        ArgumentNullException.ThrowIfNull(payload);
        uint target = _query.PickAt(mouseX, mouseY, includeSelf: true) ?? 0u;
        if (target != 0u)
            _query.BeginLightingPulse(target);
        _items.PlaceIn3D(payload, target);
    }

    public uint? GetSelectedOrClosestCombatTarget(bool autoTarget)
    {
        if (_selection.SelectedObjectId is { } selected
            && _query.IsAttackableTarget(selected))
        {
            return selected;
        }
        return autoTarget ? SelectClosestCombatTarget(showToast: false) : null;
    }

    public uint? SelectClosestCombatTarget(bool showToast)
    {
        ClosestCombatTarget? closest = _query.FindClosestHostileMonster();
        uint? bestGuid = closest?.ServerGuid;
        if (bestGuid is { } selected)
            _selection.Select(selected, SelectionChangeSource.Keyboard);
        else
            _selection.Clear(SelectionChangeSource.Keyboard);

        if (bestGuid is { } guid)
        {
            string label = _query.Describe(guid);
            float distance = MathF.Sqrt(closest!.Value.DistanceSquared);
            Console.WriteLine($"combat: selected target 0x{guid:X8} {label} dist={distance:F1}");
            if (showToast)
                _toast?.Invoke($"Target {label}");
        }
        else if (showToast)
        {
            _toast?.Invoke("No monster target");
            Console.WriteLine("combat: no creature target found");
        }
        return bestGuid;
    }

    public void PickAndStoreSelection(bool useImmediately)
    {
        uint? picked = _query.PickAtCursor(includeSelf: true);
        if (picked is not uint guid)
        {
            if (!_items.IsAnyTargetModeActive)
                _toast?.Invoke("Nothing to select");
            return;
        }

        _query.BeginLightingPulse(guid);
        if (_items.OfferPrimaryClick(guid) is not ItemPrimaryClickResult.NotActive)
            return;

        _selection.Select(guid, SelectionChangeSource.World);
        string label = _query.Describe(guid);
        Console.WriteLine($"[interaction] pick guid=0x{guid:X8} name={label}");
        _toast?.Invoke($"Selected: {label}");
        if (useImmediately && !_query.IsWieldedByPlayer(guid))
        {
            EnqueueIdentityBound(
                RuntimeQueuedInteractionKind.Activate,
                guid,
                requireLiveEntity: true);
        }
    }

    public void PickSelectAndExamine()
    {
        uint? picked = _query.PickAtCursor(includeSelf: true);
        if (picked is not uint guid)
            return;

        _query.BeginLightingPulse(guid);
        _selection.Select(guid, SelectionChangeSource.World);
        _items.ExamineSelectedOrEnterMode(guid);
    }

    public void UseCurrentSelection()
    {
        if (_selection.SelectedObjectId is not uint selected)
        {
            _toast?.Invoke("Nothing selected");
            return;
        }
        EnqueueIdentityBound(
            RuntimeQueuedInteractionKind.Use,
            selected,
            requireLiveEntity: false);
    }

    public void SendUse(uint serverGuid)
        => RequestUse(serverGuid, reservation: null);

    /// <summary>
    /// The plugin surface's entry point for using a world object it does
    /// not own (a vendor, a corpse, a chest, an NPC). Runs through the same
    /// walk-then-use path a click on that object takes -- an out-of-range
    /// target gets a queued approach that dispatches the use on arrival --
    /// instead of the inventory-only path the item automation surface uses
    /// for owned items. Unlike a click, this can arrive on any thread at
    /// any cadence a plugin chooses, so it is held to every gate a click
    /// (or an owned item's own automation entry point) is held to: the
    /// use-throttle, the one-request-at-a-time inventory gate, and "don't
    /// preempt whatever is already in flight" rather than cancelling it.
    /// </summary>
    public AutomationUseOutcome TryUseForAutomation(uint serverGuid)
    {
        // Every exit from this method is logged at Information level: it
        // is the plugin surface's only entry point for using a world
        // object it does not own, it can fire at any cadence a plugin
        // chooses, and PerformUse itself never logs for this route
        // (log: false below) -- without a line here a "Started" that
        // silently never completes (see HandleUseApproachCompletion) left
        // no trace at all in the host's own log.
        AutomationUseOutcome outcome = TryUseForAutomationCore(serverGuid);
        Console.WriteLine(
            $"[interaction] automation use guid=0x{serverGuid:X8} outcome={outcome}");
        return outcome;
    }

    private AutomationUseOutcome TryUseForAutomationCore(uint serverGuid)
    {
        if (serverGuid == 0u)
            return AutomationUseOutcome.NotUseable;
        // A plugin using another player would otherwise silently open a
        // secure trade (PerformUse's first branch, shared with the click
        // path) -- refuse it here instead; a player-to-player exchange
        // goes through the Trade surface, not Use.
        if (_items.IsPlayerTarget(serverGuid))
            return AutomationUseOutcome.NotUseable;
        if (!_items.TryConsumeUseThrottleForAutomation())
            return AutomationUseOutcome.Busy;
        if (!_items.EnsureInventoryRequestReady())
            return AutomationUseOutcome.Busy;

        ItemUseRequestReservation reservation = _items.BeginAutomationUseReservation();
        // The reservation holds the busy count from this point; a throw
        // downstream (transport fault, reset mid-call) must give it back or
        // the one-request-at-a-time gate stays wedged for the session --
        // the same shape the confirmed-use and vendor routes use.
        try
        {
            return PerformUse(
                serverGuid,
                reservation,
                toast: false,
                log: false,
                preemptPending: false);
        }
        catch
        {
            reservation.CancelBeforeDispatch();
            throw;
        }
    }

    public void RequestUse(
        uint serverGuid,
        ItemUseRequestReservation? reservation)
        => PerformUse(serverGuid, reservation, toast: true, log: true, preemptPending: true);

    private AutomationUseOutcome PerformUse(
        uint serverGuid,
        ItemUseRequestReservation? reservation,
        bool toast,
        bool log,
        bool preemptPending)
    {
        if (preemptPending)
        {
            // A new SendPickup/RequestUse from the user supersedes
            // whatever approach was previously armed (G3) -- the newest
            // click wins.
            CancelPendingApproach();
        }
        else if (_transactions.HasPendingUse || _transactions.HasPendingPickup)
        {
            // The automation path never preempts an in-flight approach or
            // pickup; it reports Busy and leaves whatever is already
            // queued (a user's own pending click, or an earlier automation
            // call) alone.
            reservation?.CancelBeforeDispatch();
            return AutomationUseOutcome.Busy;
        }

        if (_items.TryOpenSecureTradeWithPlayer(serverGuid))
        {
            reservation?.CancelBeforeDispatch();
            return AutomationUseOutcome.Started;
        }

        bool ownedByPlayer = _items.IsOwnedByPlayer(serverGuid);
        bool useable = ownedByPlayer || _query.IsUseable(serverGuid);

        if (useable
            && _query.TryGetApproach(serverGuid, out InteractionApproach approach)
            && !approach.IsCloseRange)
        {
            bool armed = false;
            bool started = _movement.BeginApproach(
                approach,
                token =>
                {
                    armed = _transactions.TryArmPostArrivalUse(
                        serverGuid,
                        ownedByPlayer,
                        useable,
                        reservation,
                        new RuntimeInteractionApproachToken(
                            token.ControllerLifetime,
                            token.ApproachGeneration),
                        out _);
                    if (armed)
                        _pendingUseArmedAtMs = _nowMs();
                });
            if (!started || !armed)
            {
                if (_transactions.TryCancelPendingUse(
                        serverGuid, out RuntimePendingUse cancelled))
                {
                    cancelled.Reservation?.CancelBeforeDispatch();
                    _pendingUseArmedAtMs = null;
                }
                else
                {
                    reservation?.CancelBeforeDispatch();
                }
                return AutomationUseOutcome.Busy;
            }
            return AutomationUseOutcome.Started;
        }

        RuntimeInteractionDispatchResult result =
            _transactions.TryDispatchUse(
                serverGuid,
                ownedByPlayer,
                useable,
                reservation,
                _transport,
                out uint sequence);
        if (result == RuntimeInteractionDispatchResult.NotInWorld)
        {
            if (toast)
                _toast?.Invoke("Not in world");
            return AutomationUseOutcome.NotInWorld;
        }
        if (result == RuntimeInteractionDispatchResult.Dispatched)
        {
            if (log)
                Console.WriteLine($"[interaction] use guid=0x{serverGuid:X8} seq={sequence}");
            return AutomationUseOutcome.Started;
        }
        if (result == RuntimeInteractionDispatchResult.NotUseable)
            return AutomationUseOutcome.NotUseable;
        // Rejected: the transport itself refused the send -- distinct from
        // the busy gates above, so a caller does not read it as "try
        // again shortly".
        return AutomationUseOutcome.Unavailable;
    }

    public void SendPickup(uint itemGuid, uint destinationContainerId, int placement)
    {
        CancelPendingApproach();
        if (!_transport.IsInWorld)
        {
            _toast?.Invoke("Not in world");
            CancelPickupPresentation(itemGuid);
            return;
        }

        ulong pendingPlacementToken = _items.TryGetPendingBackpackPlacement(
            itemGuid,
            out PendingBackpackPlacement pendingPlacement)
                ? pendingPlacement.Token
                : 0u;
        if (!IsCurrentPickupPresentation(
                itemGuid,
                destinationContainerId,
                placement,
                pendingPlacementToken))
        {
            return;
        }

        if (_items.IsInCurrentGroundObject(itemGuid)
            || (_query.IsWieldedPositionState(itemGuid)
                && _items.IsOwnedByPlayer(itemGuid)))
        {
            var contained = new RuntimePendingPickup(
                Token: 0u,
                itemGuid,
                LocalEntityId: 0u,
                destinationContainerId,
                placement,
                pendingPlacementToken,
                ApproachToken: default);
            if (_transactions.TryDispatchPickup(
                    contained,
                    _transport,
                    out uint containedSequence))
            {
                Console.WriteLine(
                    $"[interaction] contained pickup item=0x{itemGuid:X8} container=0x{destinationContainerId:X8} placement={placement} seq={containedSequence}");
            }
            else
            {
                CancelPickupPresentation(itemGuid, pendingPlacementToken);
            }
            return;
        }

        if (!ValidatePickupTarget(itemGuid, showToast: true)
            || !_query.TryGetApproach(itemGuid, out InteractionApproach approach))
        {
            CancelPickupPresentation(itemGuid, pendingPlacementToken);
            return;
        }

        if (approach.IsCloseRange)
        {
            bool armed = false;
            bool started = _movement.BeginApproach(
                approach,
                token =>
                {
                    armed = _transactions.TryArmPostArrivalPickup(
                        itemGuid,
                        approach.Target.LocalEntityId,
                        destinationContainerId,
                        placement,
                        pendingPlacementToken,
                        new RuntimeInteractionApproachToken(
                            token.ControllerLifetime,
                            token.ApproachGeneration),
                        out _);
                });
            if (!started || !armed)
            {
                if (_transactions.TryCancelPendingPickup(
                        itemGuid,
                        approach.Target.LocalEntityId,
                        out RuntimePendingPickup cancelled))
                {
                    CancelPickupPresentation(
                        cancelled.ServerGuid,
                        cancelled.PendingPlacementToken);
                }
                else
                {
                    CancelPickupPresentation(itemGuid, pendingPlacementToken);
                }
            }
            return;
        }

        _movement.BeginApproach(approach);
        if (!IsCurrentPickupPresentation(
                itemGuid,
                destinationContainerId,
                placement,
                pendingPlacementToken))
        {
            return;
        }
        var immediate = new RuntimePendingPickup(
            Token: 0u,
            itemGuid,
            approach.Target.LocalEntityId,
            destinationContainerId,
            placement,
            pendingPlacementToken,
            ApproachToken: default);
        if (_transactions.TryDispatchPickup(
                immediate,
                _transport,
                out uint sequence))
        {
            Console.WriteLine(
                $"[interaction] pickup item=0x{itemGuid:X8} container=0x{destinationContainerId:X8} placement={placement} seq={sequence}");
        }
        else
        {
            CancelPickupPresentation(itemGuid, pendingPlacementToken);
        }
    }

    public void OnNaturalMoveToComplete()
    {
        if (_transactions.TryGetPendingPickup(out RuntimePendingPickup pendingPickup))
        {
            HandleApproachCompletion(pendingPickup.ApproachToken, natural: true);
            return;
        }
        if (_transactions.TryGetPendingUse(out RuntimePendingUse pendingUse))
            HandleApproachCompletion(pendingUse.ApproachToken, natural: true);
    }

    private void HandleApproachCompletion(
        RuntimeInteractionApproachToken approachToken,
        bool natural)
    {
        bool pickupAccepted = _transactions.TryResolveApproachCompletion(
            approachToken,
            natural,
            out RuntimePendingPickup pendingPickup);
        if (pendingPickup.Token != 0u)
        {
            HandlePickupApproachCompletion(pendingPickup, pickupAccepted);
            return;
        }

        bool useAccepted = _transactions.TryResolveUseApproachCompletion(
            approachToken,
            natural,
            out RuntimePendingUse pendingUse);
        if (pendingUse.Token != 0u)
            HandleUseApproachCompletion(pendingUse, useAccepted);
    }

    private void HandlePickupApproachCompletion(
        RuntimePendingPickup pending,
        bool accepted)
    {
        if (!accepted)
        {
            CancelPickupPresentation(
                pending.ServerGuid,
                pending.PendingPlacementToken);
            return;
        }
        if (!_query.IsCurrent(pending.ServerGuid, pending.LocalEntityId))
        {
            CancelPickupPresentation(
                pending.ServerGuid,
                pending.PendingPlacementToken);
            return;
        }

        if (!IsCurrentPickupPresentation(
                pending.ServerGuid,
                pending.DestinationContainerId,
                pending.Placement,
                pending.PendingPlacementToken))
        {
            return;
        }
        if (!_transactions.TryDispatchPickup(
                pending,
                _transport,
                out _))
        {
            CancelPickupPresentation(
                pending.ServerGuid,
                pending.PendingPlacementToken);
        }
    }

    private void HandleUseApproachCompletion(
        RuntimePendingUse pending,
        bool accepted)
    {
        _pendingUseArmedAtMs = null;
        if (!accepted)
        {
            pending.Reservation?.CancelBeforeDispatch();
            return;
        }

        // The walk reported arrival, but the local physics can report
        // "movement complete" once it can make no further progress toward
        // the target -- an obstruction (a closed door, a wall) in the
        // straight-line approach path stops the walk short of actual use
        // range with no error of its own. Dispatching Use from there sends
        // a request the server's own range check silently drops, so the
        // caller (a plugin's Started outcome, or a click) never learns the
        // interaction failed and the reservation would otherwise wait
        // forever for a completion that already happened. Re-verify the
        // player is actually within use range before sending.
        if (!_query.TryGetApproach(pending.ServerGuid, out InteractionApproach arrived)
            || !arrived.IsCloseRange)
        {
            pending.Reservation?.CancelBeforeDispatch();
            Console.WriteLine(
                $"[interaction] use guid=0x{pending.ServerGuid:X8} arrival short of use range -- refused");
            _toast?.Invoke("You are too far away to do that.");
            return;
        }

        RuntimeInteractionDispatchResult result =
            _transactions.TryDispatchUse(
                pending.ServerGuid,
                pending.OwnedByPlayer,
                pending.Useable,
                pending.Reservation,
                _transport,
                out uint sequence);
        if (result == RuntimeInteractionDispatchResult.NotInWorld)
            _toast?.Invoke("Not in world");
        if (result == RuntimeInteractionDispatchResult.Dispatched)
        {
            Console.WriteLine(
                $"[interaction] use guid=0x{pending.ServerGuid:X8} seq={sequence} (arrival-gated)");
        }
    }

    public void DrainOutbound()
    {
        while (_approachCompletions.TryTake(out PlayerApproachCompletion completion))
        {
            HandleApproachCompletion(
                new RuntimeInteractionApproachToken(
                    completion.Token.ControllerLifetime,
                    completion.Token.ApproachGeneration),
                completion.IsNatural);
        }
        ExpireStalePendingUse();
        _transactions.DrainOutbound(DispatchQueuedInteraction);
    }

    /// <summary>
    /// Forces a definite outcome on an armed walk-then-use that has waited
    /// past <see cref="PendingUseArrivalTimeoutMs"/> for an arrival signal
    /// that never came (see the constant's own comment for why that can
    /// happen). Without this, an obstructed target left the reservation
    /// held and HasPendingUse true for the rest of the session -- every
    /// later Use, from a click or a plugin, reported Busy forever.
    /// </summary>
    private void ExpireStalePendingUse()
    {
        if (_pendingUseArmedAtMs is not { } armedAt
            || _nowMs() - armedAt < PendingUseArrivalTimeoutMs)
        {
            return;
        }

        _pendingUseArmedAtMs = null;
        if (_transactions.TryCancelPendingUse(out RuntimePendingUse pending))
        {
            pending.Reservation?.CancelBeforeDispatch();
            Console.WriteLine(
                $"[interaction] use guid=0x{pending.ServerGuid:X8} arrival timed out after {PendingUseArrivalTimeoutMs}ms -- refused");
            _toast?.Invoke("You are too far away to do that.");
        }
    }

    public void OnMoveToCancelled(WeenieError _) => CancelPendingApproach();

    public void OnEntityHidden(uint serverGuid)
    {
        _transactions.CancelQueuedInteractions(serverGuid);
        if (_transactions.TryCancelPendingPickup(
                serverGuid,
                localEntityId: null,
                out RuntimePendingPickup cancelled))
        {
            CancelPickupPresentation(
                cancelled.ServerGuid,
                cancelled.PendingPlacementToken);
        }
        if (_transactions.TryCancelPendingUse(serverGuid, out RuntimePendingUse cancelledUse))
        {
            cancelledUse.Reservation?.CancelBeforeDispatch();
            _pendingUseArmedAtMs = null;
        }
        if (_selection.SelectedObjectId == serverGuid)
        {
            _selection.Clear(
                SelectionChangeSource.System,
                SelectionChangeReason.Cleared);
        }
    }

    public void OnEntityRemoved(LiveEntityRecord record, bool replacementExists)
    {
        ArgumentNullException.ThrowIfNull(record);
        _transactions.CancelQueuedInteractions(
            record.ServerGuid,
            record.LocalEntityId);
        if (_transactions.TryCancelPendingPickup(
                record.ServerGuid,
                record.LocalEntityId,
                out RuntimePendingPickup cancelled))
        {
            CancelPickupPresentation(
                cancelled.ServerGuid,
                cancelled.PendingPlacementToken);
        }
        if (_transactions.TryCancelPendingUse(record.ServerGuid, out RuntimePendingUse cancelledUse))
        {
            cancelledUse.Reservation?.CancelBeforeDispatch();
            _pendingUseArmedAtMs = null;
        }
        if (!replacementExists && _selection.SelectedObjectId == record.ServerGuid)
        {
            _selection.Clear(
                SelectionChangeSource.System,
                SelectionChangeReason.SelectedObjectRemoved);
        }
    }

    public void ResetSession()
    {
        List<Exception> failures = [];
        try { CancelPendingApproach(); }
        catch (Exception error) { failures.Add(error); }
        try { _items.ResetSession(); }
        catch (Exception error) { failures.Add(error); }
        try { _selection.Reset(); }
        catch (Exception error) { failures.Add(error); }
        try { _approachCompletions.Clear(); }
        catch (Exception error) { failures.Add(error); }

        if (failures.Count != 0)
            throw new AggregateException(
                "One or more selection-interaction reset stages failed.",
                failures);
    }

    internal void ResetGenerationPresentation()
    {
        List<Exception> failures = [];
        try { CancelPendingApproach(); }
        catch (Exception error) { failures.Add(error); }
        try { _items.ResetGenerationPresentation(); }
        catch (Exception error) { failures.Add(error); }
        try { _approachCompletions.Clear(); }
        catch (Exception error) { failures.Add(error); }

        if (failures.Count != 0)
        {
            throw new AggregateException(
                "One or more selection-interaction presentation reset stages failed.",
                failures);
        }
    }

    private bool ValidatePickupTarget(uint serverGuid, bool showToast)
    {
        if (_query.IsCreature(serverGuid))
        {
            if (showToast)
                _toast?.Invoke(RetailMessages.CannotPickUpCreatures);
            return false;
        }
        if (_query.IsStuckInWorld(serverGuid))
        {
            if (showToast)
                _toast?.Invoke(RetailMessages.CannotBePickedUp(_query.Describe(serverGuid)));
            return false;
        }
        if (_query.IsWieldedPositionState(serverGuid)
            && !_items.IsOwnedByPlayer(serverGuid))
        {
            if (showToast)
            {
                _toast?.Invoke(RetailMessages.BeingWieldedBySomeoneElse(
                    _query.Describe(serverGuid)));
            }
            return false;
        }
        if (_query.IsPickupable(serverGuid))
            return true;
        if (showToast)
            _toast?.Invoke(RetailMessages.CantBePickedUp(_query.Describe(serverGuid)));
        return false;
    }

    private bool EnqueueIdentityBound(
        RuntimeQueuedInteractionKind kind,
        uint serverGuid,
        bool requireLiveEntity)
    {
        uint? localEntityId = _query.TryCaptureIdentity(serverGuid, out uint localId)
            ? localId
            : null;
        ClientObject? item = _items.TryCaptureObjectIdentity(serverGuid, out ClientObject captured)
            ? captured
            : null;
        if ((requireLiveEntity && localEntityId is null)
            || (localEntityId is null && item is null))
        {
            return false;
        }

        var identity = new RuntimeInteractionIdentity(
            serverGuid,
            localEntityId,
            item);
        _transactions.Enqueue(new RuntimeQueuedInteraction(kind, identity));
        return true;
    }

    private bool IsCurrent(RuntimeInteractionIdentity identity)
        => (identity.LocalEntityId is not uint localId
                || _query.IsCurrent(identity.ServerGuid, localId))
            && (identity.ClientObject is not { } item
                || _items.IsCurrentObjectIdentity(identity.ServerGuid, item));

    private void CancelPendingApproach()
    {
        if (_transactions.TryCancelPendingPickup(
                out RuntimePendingPickup pending))
        {
            CancelPickupPresentation(
                pending.ServerGuid,
                pending.PendingPlacementToken);
        }
        // G3: a new SendPickup/RequestUse supersedes whatever approach was
        // previously armed — release an in-flight Use's reservation too, not
        // just pickup's presentation token.
        if (_transactions.TryCancelPendingUse(out RuntimePendingUse pendingUse))
        {
            pendingUse.Reservation?.CancelBeforeDispatch();
            _pendingUseArmedAtMs = null;
        }
    }

    private void DispatchQueuedInteraction(
        RuntimeQueuedInteraction interaction)
    {
        RuntimeInteractionIdentity identity = interaction.Identity;
        if (!IsCurrent(identity))
            return;

        switch (interaction.Kind)
        {
            case RuntimeQueuedInteractionKind.Activate:
                _items.ActivateItem(identity.ServerGuid);
                break;
            case RuntimeQueuedInteractionKind.Use:
                _items.UseSelectedOrEnterMode(identity.ServerGuid);
                break;
            case RuntimeQueuedInteractionKind.Pickup:
                if (ValidatePickupTarget(identity.ServerGuid, showToast: true))
                    _items.PlaceWorldItemInBackpack(identity.ServerGuid);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown queued interaction kind {interaction.Kind}.");
        }
    }

    private void CancelPickupPresentation(uint itemGuid, ulong token = 0u)
        => _items.CancelPendingBackpackPlacement(itemGuid, token);

    private bool IsCurrentPickupPresentation(
        uint itemGuid,
        uint destinationContainerId,
        int placement,
        ulong token)
        => token != 0u
            && _items.TryGetPendingBackpackPlacement(
                itemGuid,
                out PendingBackpackPlacement pending)
            && pending.Token == token
            && pending.ContainerId == destinationContainerId
            && pending.Placement == placement;
}
