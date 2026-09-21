using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// Why an attack found nothing to go to.
/// </summary>
public enum RuntimeAttackTargetRefusal
{
    /// <summary>A target was found; nothing was refused.</summary>
    None,

    /// <summary>Nothing was selected and no substitute was allowed.</summary>
    NothingSelected,

    /// <summary>
    /// Something is selected, but it is not a creature this character may
    /// attack: another player outside a shared player-killer status, a pet,
    /// an object, or a monster that has left play.
    /// </summary>
    SelectionNotAttackable,

    /// <summary>
    /// A substitute was allowed and there was no monster to pick: none that
    /// can be seen and still has health.
    /// </summary>
    NoMonsterFound,
}

/// <param name="Target">The creature the attack goes to, when there is one.</param>
/// <param name="Refusal">Why there is none.</param>
public readonly record struct RuntimeAttackTargetResolution(
    uint? Target,
    RuntimeAttackTargetRefusal Refusal);

/// <summary>
/// The one answer to "which creature does this attack go to".
///
/// Both hosts ask it, so a target a plugin names is the target that is swung
/// at whether or not there is a window, and a refusal means the same thing in
/// both. The rule is the player's: the selected creature when it can be
/// attacked, else - only when the caller allows a substitute and the
/// character's auto-target option is on - the nearest monster, which becomes
/// the selection exactly as it would had the player picked it.
/// </summary>
public static class RuntimeAttackTargetResolver
{
    public static RuntimeAttackTargetResolution Resolve(
        GameRuntime runtime,
        bool allowAutoTarget)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        uint? selected = runtime.ActionOwner.Selection.SelectedObjectId;
        // A selection the player made themselves is judged by the wider
        // selected-object rule: it takes in a player they are entitled to
        // attack, which the monster scan never offers.
        if (selected is { } explicitTarget
            && RuntimeHostileTargetQuery.IsAttackableSelection(
                runtime,
                explicitTarget))
        {
            return new(explicitTarget, RuntimeAttackTargetRefusal.None);
        }

        if (!allowAutoTarget || !AutoTargetEnabled(runtime))
        {
            return new(
                null,
                selected is null
                    ? RuntimeAttackTargetRefusal.NothingSelected
                    : RuntimeAttackTargetRefusal.SelectionNotAttackable);
        }

        return SelectClosest(runtime) is { } acquired
            ? new(acquired, RuntimeAttackTargetRefusal.None)
            : new(null, RuntimeAttackTargetRefusal.NoMonsterFound);
    }

    /// <summary>
    /// Picks the nearest monster a player could pick and makes it the
    /// selection, or clears the selection when there is none. This is what a
    /// select-nearest key does, and what an auto-target substitution does.
    /// </summary>
    public static uint? SelectClosest(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        uint? closest = RuntimeHostileTargetQuery.FindClosest(
            runtime,
            HostileTargetScope.Selectable);
        if (closest is { } acquired)
        {
            runtime.ActionOwner.Selection.Select(
                acquired,
                SelectionChangeSource.Keyboard);
        }
        else
        {
            runtime.ActionOwner.Selection.Clear(SelectionChangeSource.Keyboard);
        }
        return closest;
    }

    public static bool AutoTargetEnabled(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.CharacterOwner.Options.GetOptionBit(
            CharacterOptionId.AutoTarget);
    }
}
