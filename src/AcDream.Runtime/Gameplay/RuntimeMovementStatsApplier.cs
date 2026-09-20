namespace AcDream.Runtime.Gameplay;

/// <summary>
/// Notices the moment the character runs out of stamina, and the moment it
/// stops being out. Only the crossing matters: the character is told once,
/// not on every update that says the same thing.
/// </summary>
internal sealed class StaminaExhaustionEdgeTracker
{
    private bool? _wasExhausted;

    /// <summary>
    /// What the tracker currently believes, or nothing when it has not been
    /// told yet -- which is also its state at the start of a generation.
    /// </summary>
    public bool? Opinion => _wasExhausted;

    public bool Observe(int currentStamina)
    {
        bool exhausted = currentStamina == 0;
        if (_wasExhausted == exhausted)
            return false;

        bool isEdge = _wasExhausted.HasValue;
        _wasExhausted = exhausted;
        return isEdge;
    }

    public void Reset() => _wasExhausted = null;
}

/// <summary>
/// Re-derives how fast the character runs and jumps from what the server last
/// said about its skills, burden and stamina.
///
/// One owner for every client: a raise that changes the run skill has to
/// change how fast the character moves whether or not anyone is watching it
/// move, and a client that skipped this read its own speed from stale
/// numbers for the rest of the session.
/// </summary>
internal sealed class RuntimeMovementStatsApplier(
    RuntimeLocalPlayerMovementState movement,
    RuntimeMovementSkillState skills,
    Action<string> log)
{
    private readonly RuntimeLocalPlayerMovementState _movement = movement
        ?? throw new ArgumentNullException(nameof(movement));
    private readonly RuntimeMovementSkillState _skills = skills
        ?? throw new ArgumentNullException(nameof(skills));
    private readonly Action<string> _log = log
        ?? throw new ArgumentNullException(nameof(log));
    private readonly StaminaExhaustionEdgeTracker _staminaExhaustion = new();

    public void Reset() => _staminaExhaustion.Reset();

    /// <summary>
    /// Whether this owner has an opinion yet about the character being out of
    /// stamina. Nothing means the next update that says so is a crossing.
    /// </summary>
    internal bool? StaminaOpinion => _staminaExhaustion.Opinion;

    public RuntimeMovementStatsApplication Apply(string reason)
    {
        RuntimeMovementStatsApplication outcome =
            _movement.ApplyCharacterMovementStats(_skills);
        switch (outcome)
        {
            case RuntimeMovementStatsApplication.DroppedNoController:
            case RuntimeMovementStatsApplication.DroppedIncompleteSnapshot:
                // There is nothing to apply it to yet, and nothing to say.
                return outcome;
            case RuntimeMovementStatsApplication.DroppedDisplacedController:
                _log(
                    $"player: dropped displaced movement {reason} — the "
                    + "movement controller is terminal");
                return outcome;
        }

        RuntimeMovementSkillSnapshot snapshot = _skills.Snapshot;
        if (_staminaExhaustion.Observe(snapshot.CurrentStamina)
            && outcome is RuntimeMovementStatsApplication.AppliedLive)
        {
            _movement.ReportExhaustion();
        }

        _log(
            $"player: applied server movement {reason} "
            + $"run={snapshot.RunSkill} jump={snapshot.JumpSkill} "
            + $"burden={snapshot.Burden:F2} stamina={snapshot.CurrentStamina}");
        return outcome;
    }
}
