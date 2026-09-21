using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Spells;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A runtime for tests about the wiring itself rather than about play: the
/// gameplay operations answer nothing, because nothing here asks them to.
/// Scenario arms use their host's real operations instead.
/// </summary>
internal static class HarnessRuntime
{
    internal static GameRuntimeDependencies Dependencies()
    {
        var inert = new InertGameplayOperations();
        return new GameRuntimeDependencies(inert, inert, inert, inert);
    }

    private sealed class InertGameplayOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        public bool CanStartAttack(bool allowAutoTarget) => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power, bool allowAutoTarget) =>
            false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId, SpellMetadata spell, bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
