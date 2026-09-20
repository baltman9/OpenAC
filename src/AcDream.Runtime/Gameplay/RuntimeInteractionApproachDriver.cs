namespace AcDream.Runtime.Gameplay;

/// <summary>
/// The once-a-frame step of a walk the character was sent on in order to act
/// on something. Every client takes this step in the same place in its frame,
/// with or without anything to draw on.
/// </summary>
public interface IRuntimeArmedApproachDrive
{
    /// <summary>
    /// Ends the walks that have ended: a walk that arrived sends what was
    /// armed for the arrival, a walk that was called off releases it, and a
    /// walk that has stopped getting anywhere is given up on.
    /// </summary>
    void DriveArmedApproaches();
}

/// <summary>
/// What a client that has something to draw on adds to a walk-to-then-act:
/// the walk-then-pickup route, whose outcome is a change to what is drawn,
/// and the words a person who clicked is told when the walk does not work
/// out. A client with nothing to draw on has neither, and the walk-then-use
/// half below runs the same way regardless.
/// </summary>
internal interface IRuntimeInteractionArrivalPresentation
{
    /// <summary>The walk armed for a pickup has ended.</summary>
    /// <param name="pending">What was armed.</param>
    /// <param name="accepted">Whether the walk arrived rather than being called off.</param>
    void CompletePickupApproach(RuntimePendingPickup pending, bool accepted);

    /// <summary>
    /// Gives up on an armed pickup whose walk has stopped getting anywhere.
    /// </summary>
    /// <param name="stalledTicks">How long the walk has been stalled.</param>
    void GiveUpOnStalledPickupApproach(uint stalledTicks);

    /// <summary>Tells the person who asked how it went, in words.</summary>
    /// <param name="text">What to tell them.</param>
    void Say(string text);
}

/// <summary>
/// Drives the end of an armed walk: it takes the "the walk ended" answers the
/// body leaves behind, sends the use that was armed for arrival, and gives up
/// on a walk that has stopped getting anywhere so the one-request-at-a-time
/// gate is not held for the rest of the session.
///
/// One owner, driven from the one place both clients take their per-frame
/// local-player step, because a plugin asking to open a corpse a few metres
/// off must get the same walk AND the same use whether or not there is a
/// window. This used to be driven only by the client that draws, so a client
/// with nothing to draw on walked to the corpse and stood there.
/// </summary>
internal sealed class RuntimeInteractionApproachDriver : IRuntimeArmedApproachDrive
{
    private readonly RuntimeApproachCompletionState _completions;
    private readonly RuntimeInteractionTransactionState _transactions;
    private readonly RuntimeWorldObjectUse _worldObjectUse;
    private readonly IRuntimeApproachSource _approach;
    private readonly Action<string>? _log;
    private IRuntimeInteractionArrivalPresentation? _presentation;

    internal RuntimeInteractionApproachDriver(
        RuntimeApproachCompletionState completions,
        RuntimeInteractionTransactionState transactions,
        RuntimeWorldObjectUse worldObjectUse,
        IRuntimeApproachSource approach,
        Action<string>? log = null)
    {
        _completions = completions
            ?? throw new ArgumentNullException(nameof(completions));
        _transactions = transactions
            ?? throw new ArgumentNullException(nameof(transactions));
        _worldObjectUse = worldObjectUse
            ?? throw new ArgumentNullException(nameof(worldObjectUse));
        _approach = approach ?? throw new ArgumentNullException(nameof(approach));
        _log = log;
    }

    /// <summary>
    /// Hands the driver the walk-then-pickup route and the voice a client with
    /// something to draw on has. At most one of those exists at a time.
    /// </summary>
    /// <param name="presentation">That client's own half.</param>
    /// <returns>A lease that takes it back again.</returns>
    internal IDisposable BindPresentationOwned(
        IRuntimeInteractionArrivalPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (Interlocked.CompareExchange(ref _presentation, presentation, null)
            is not null)
        {
            throw new InvalidOperationException(
                "An arrival presentation is already bound.");
        }
        return new PresentationLease(this, presentation);
    }

    /// <inheritdoc />
    public void DriveArmedApproaches()
    {
        while (_completions.TryTake(out RuntimeApproachCompletion completion))
        {
            ResolveApproachCompletion(
                new RuntimeInteractionApproachToken(
                    completion.Token.ControllerLifetime,
                    completion.Token.ApproachGeneration),
                completion.IsNatural);
        }
        ExpireStalledApproach();
    }

    /// <summary>
    /// Acts on one walk that has ended: whatever was armed for it, if it is
    /// still the walk that was armed.
    /// </summary>
    /// <param name="approachToken">Which walk ended.</param>
    /// <param name="natural">Whether it arrived rather than being called off.</param>
    internal void ResolveApproachCompletion(
        RuntimeInteractionApproachToken approachToken,
        bool natural)
    {
        bool pickupAccepted = _transactions.TryResolveApproachCompletion(
            approachToken,
            natural,
            out RuntimePendingPickup pendingPickup);
        if (pendingPickup.Token != 0u)
        {
            IRuntimeInteractionArrivalPresentation? presentation = _presentation;
            if (presentation is null)
            {
                // Arming a pickup for arrival is the drawing client's own
                // route, so a pending one with nobody to finish it means the
                // route was armed and its owner has gone. Say so rather than
                // quietly dropping the reservation it holds.
                _log?.Invoke(
                    "[interaction] a pickup armed for arrival has no route to "
                    + "finish it");
                return;
            }
            presentation.CompletePickupApproach(pendingPickup, pickupAccepted);
            return;
        }

        bool useAccepted = _transactions.TryResolveUseApproachCompletion(
            approachToken,
            natural,
            out RuntimePendingUse pendingUse);
        if (pendingUse.Token == 0u)
            return;
        if (_worldObjectUse.CompleteArmedUse(pendingUse, useAccepted)
            == RuntimeInteractionDispatchResult.NotInWorld)
        {
            _presentation?.Say("Not in world");
        }
    }

    /// <summary>
    /// Forces a definite outcome on an armed walk whose move-to has stopped
    /// getting closer for
    /// <see cref="RuntimeWorldObjectUse.StalledApproachGiveUpTicks"/> ticks in
    /// a row with no arrival answer (the walk itself has no give-up threshold
    /// of its own, and must keep none). Without this, an obstructed target
    /// left the reservation held and a use or pickup pending for the rest of
    /// the session -- every later one, from a click or a plugin, reported busy
    /// forever. Only one of the two can be pending at a time (arming either
    /// always supersedes or is refused against the other), so one shared read
    /// of the walk's stall counter judges both.
    /// </summary>
    private void ExpireStalledApproach()
    {
        if (_approach.StalledTicks() is not { } stalledTicks)
        {
            // No walk is in progress at all. This should be unreachable while
            // a use or pickup is still pending: arming either always starts a
            // walk, and both the arrival and the call-off publish an answer
            // unconditionally, which clears the pending state either way. If
            // this ever appears, the bug is upstream of here -- a pending
            // state that survived a walk which ended some other way -- so say
            // so rather than guessing at a watchdog over an invariant that
            // should not break.
            if (_transactions.HasPendingUse || _transactions.HasPendingPickup)
            {
                _log?.Invoke(
                    "[interaction] invariant violation: a pending use or "
                    + "pickup is armed with no active move-to in progress");
            }
            return;
        }

        if (stalledTicks < RuntimeWorldObjectUse.StalledApproachGiveUpTicks)
            return;

        // Giving up on an armed use, and stopping the character walking into
        // whatever is in the way, is the shared route's own business.
        if (_worldObjectUse.TryGiveUpOnStalledUse(
                stalledTicks,
                out _,
                out bool spokenFor))
        {
            if (spokenFor)
                _presentation?.Say("Your approach never completed.");
            return;
        }

        _presentation?.GiveUpOnStalledPickupApproach(stalledTicks);
    }

    private sealed class PresentationLease(
        RuntimeInteractionApproachDriver owner,
        IRuntimeInteractionArrivalPresentation expected) : IDisposable
    {
        public void Dispose() =>
            Interlocked.CompareExchange(ref owner._presentation, null, expected);
    }
}
