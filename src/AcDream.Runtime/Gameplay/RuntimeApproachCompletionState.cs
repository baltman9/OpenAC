using AcDream.Core.Physics;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// Told when the walk the character was sent on either arrived or was called
/// off. One of these belongs to whichever body is currently the character's,
/// so an answer about a walk begun for a body that has since been replaced can
/// be told apart from an answer about the current one.
/// </summary>
internal interface IRuntimeApproachCompletionSink
{
    /// <summary>The walk reached what it was sent to.</summary>
    void PublishNaturalCompletion();

    /// <summary>The walk ended without arriving.</summary>
    /// <param name="error">Why it ended.</param>
    void PublishCancellation(WeenieError error);
}

/// <summary>
/// Hands out one <see cref="IRuntimeApproachCompletionSink"/> per body the
/// character takes, and takes it back when that body goes away.
/// </summary>
internal interface IRuntimeApproachCompletionLifetimeOwner
{
    /// <summary>Begins the run of walks belonging to a newly taken body.</summary>
    /// <returns>Where that body's walks report their ends.</returns>
    IRuntimeApproachCompletionSink BeginControllerLifetime();

    /// <summary>Ends that run, discarding answers still queued for it.</summary>
    /// <param name="lifetime">The sink <see cref="BeginControllerLifetime"/> returned.</param>
    void RetireControllerLifetime(IRuntimeApproachCompletionSink lifetime);
}

/// <summary>Whoever can begin a walk and name it.</summary>
internal interface IRuntimeApproachTokenSource
{
    /// <summary>
    /// Names the walk about to begin, so whatever is armed to happen on
    /// arrival can be matched to the answer that comes back.
    /// </summary>
    /// <param name="token">The name of the walk.</param>
    /// <returns>False when the character has no body to walk.</returns>
    bool TryBeginApproach(out RuntimeInteractionApproachToken token);
}

/// <summary>
/// How a walk-to-then-act ended, queued until whoever armed the act reads it.
/// </summary>
/// <param name="Token">Which walk this is about.</param>
/// <param name="IsNatural">Whether the walk arrived rather than being called off.</param>
/// <param name="Error">Why it ended, when it did not arrive.</param>
internal readonly record struct RuntimeApproachCompletion(
    RuntimeInteractionApproachToken Token,
    bool IsNatural,
    WeenieError Error);

/// <summary>
/// The queue of "the walk ended" answers, kept between the body that walks and
/// whoever armed something to happen when it arrives. Answers belonging to a
/// body the character no longer has are dropped rather than delivered late,
/// which is what keeps an act armed for one body from firing for the next.
/// </summary>
internal sealed class RuntimeApproachCompletionState
    : IRuntimeApproachCompletionLifetimeOwner,
      IRuntimeApproachTokenSource
{
    private readonly Queue<RuntimeApproachCompletion> _pending = new();
    private ControllerLifetime? _active;
    private ulong _nextLifetime;

    /// <inheritdoc />
    public IRuntimeApproachCompletionSink BeginControllerLifetime()
    {
        if (_active is not null)
            throw new InvalidOperationException(
                "An approach-completion lifetime is already active.");

        var lifetime = new ControllerLifetime(this, ++_nextLifetime);
        _active = lifetime;
        return lifetime;
    }

    /// <inheritdoc />
    public void RetireControllerLifetime(IRuntimeApproachCompletionSink lifetime)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        if (!ReferenceEquals(_active, lifetime)
            || lifetime is not ControllerLifetime retiring)
            return;

        PurgeLifetime(retiring.LifetimeId);
        if (retiring.TryGetCurrentToken(out RuntimeInteractionApproachToken token))
        {
            _pending.Enqueue(new RuntimeApproachCompletion(
                token,
                IsNatural: false,
                WeenieError.ActionCancelled));
        }
        _active = null;
    }

    /// <inheritdoc />
    public bool TryBeginApproach(out RuntimeInteractionApproachToken token)
    {
        if (_active is null)
        {
            token = default;
            return false;
        }

        token = _active.BeginApproach();
        return true;
    }

    /// <summary>Takes the next answer, if one is waiting.</summary>
    /// <param name="completion">How that walk ended.</param>
    /// <returns>False when nothing is waiting.</returns>
    public bool TryTake(out RuntimeApproachCompletion completion) =>
        _pending.TryDequeue(out completion);

    /// <summary>Forgets the current body and every answer still queued.</summary>
    public void Clear()
    {
        _active = null;
        _pending.Clear();
    }

    private void Publish(
        ControllerLifetime lifetime,
        bool isNatural,
        WeenieError error)
    {
        if (!ReferenceEquals(_active, lifetime)
            || !lifetime.TryGetCurrentToken(
                out RuntimeInteractionApproachToken token))
        {
            return;
        }

        _pending.Enqueue(new RuntimeApproachCompletion(token, isNatural, error));
    }

    private void PurgeLifetime(ulong lifetimeId)
    {
        int retained = _pending.Count;
        for (int i = 0; i < retained; i++)
        {
            RuntimeApproachCompletion completion = _pending.Dequeue();
            if (completion.Token.ControllerLifetime != lifetimeId)
                _pending.Enqueue(completion);
        }
    }

    private sealed class ControllerLifetime(
        RuntimeApproachCompletionState owner,
        ulong lifetimeId) : IRuntimeApproachCompletionSink
    {
        private ulong _approachGeneration;

        internal ulong LifetimeId => lifetimeId;

        internal RuntimeInteractionApproachToken BeginApproach() =>
            new(lifetimeId, ++_approachGeneration);

        internal bool TryGetCurrentToken(
            out RuntimeInteractionApproachToken token)
        {
            token = new RuntimeInteractionApproachToken(
                lifetimeId,
                _approachGeneration);
            return _approachGeneration != 0;
        }

        public void PublishNaturalCompletion() =>
            owner.Publish(this, isNatural: true, WeenieError.None);

        public void PublishCancellation(WeenieError error) =>
            owner.Publish(this, isNatural: false, error);
    }
}
