using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.App.Combat;

/// <summary>
/// The graphical host's way in to the one attack-target owner. It holds no
/// rule of its own: a window must not decide differently from a windowless
/// session which creature an attack goes to, or a plugin that names a target
/// would get one answer here and another there.
/// </summary>
internal sealed class CombatAttackTargetSource : ICombatAttackTargetSource
{
    private readonly GameRuntime _runtime;

    public CombatAttackTargetSource(GameRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public uint? GetSelectedOrClosestCombatTarget(bool allowAutoTarget)
    {
        uint? before = _runtime.ActionOwner.Selection.SelectedObjectId;
        RuntimeAttackTargetResolution resolution =
            RuntimeAttackTargetResolver.Resolve(_runtime, allowAutoTarget);
        if (resolution.Target is { } acquired && acquired != before)
        {
            string? name = _runtime.InventoryOwner.Objects.Get(acquired)?.Name;
            Console.WriteLine(
                $"combat: selected target 0x{acquired:X8} "
                + (string.IsNullOrWhiteSpace(name) ? string.Empty : name));
        }
        return resolution.Target;
    }
}
