using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Properties;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// What a combat-mode change asks before it goes out, answered from the
/// runtime's own owners: the body's motion state, whether a teleport is under
/// way, and whether the character's combat maneuver table is known.
/// </summary>
internal sealed class RuntimeCombatModeReadiness(GameRuntime runtime)
    : IRuntimeCombatModeReadiness
{
    private readonly GameRuntime _runtime =
        runtime ?? throw new ArgumentNullException(nameof(runtime));

    public bool IsInReadyPosition(CombatMode mode) =>
        _runtime.MovementOwner.IsInReadyPosition(
            mode,
            lenient: false,
            hasCombatTable: HasCombatTable());

    public bool IsTeleportInProgress =>
        _runtime.TransitOwner.IsTeleportActive
        || _runtime.TransitOwner.HasPendingTeleportStart;

    private bool HasCombatTable()
    {
        ClientObject? player = _runtime.InventoryOwner.Objects.Get(
            _runtime.PlayerIdentity.ServerGuid);
        return player is not null
            && player.Properties.DataIds.TryGetValue(
                (uint)PropertyDataId.CombatTable, out uint table)
            && table != 0u;
    }
}
