using AcDream.Core.Combat;
using AcDream.Core.Items;

namespace AcDream.Core.Tests.Combat;

public sealed class CombatInputPlannerTests
{
    [Fact]
    public void GetDefaultCombatMode_MissileCombatUseSelectsMissile()
    {
        var bow = Equipped(EquipMask.MissileWeapon, ItemType.MissileWeapon, combatUse: 2);

        Assert.Equal(
            CombatMode.Missile,
            CombatInputPlanner.GetDefaultCombatMode([bow]));
    }

    [Fact]
    public void GetDefaultCombatMode_PrimaryWeaponOrderMatchesRetailInventoryPlacement()
    {
        var bow = Equipped(EquipMask.MissileWeapon, ItemType.MissileWeapon, combatUse: 2);
        var sword = Equipped(EquipMask.MeleeWeapon, ItemType.MeleeWeapon, combatUse: 1);

        Assert.Equal(
            CombatMode.Missile,
            CombatInputPlanner.GetDefaultCombatMode([bow, sword]));
        Assert.Equal(
            CombatMode.Melee,
            CombatInputPlanner.GetDefaultCombatMode([sword, bow]));
    }

    [Fact]
    public void GetDefaultCombatMode_HeldCasterSelectsMagic()
    {
        var wand = Equipped(EquipMask.Held, ItemType.Caster, combatUse: 0);

        Assert.Equal(
            CombatMode.Magic,
            CombatInputPlanner.GetDefaultCombatMode([wand]));
    }

    [Fact]
    public void GetDefaultCombatMode_HeldNonCasterCannotEnterCombat()
    {
        var held = Equipped(EquipMask.Held, ItemType.Misc, combatUse: 0);

        DefaultCombatModeDecision decision =
            CombatInputPlanner.GetDefaultCombatModeDecision([held]);

        Assert.Equal(CombatMode.NonCombat, decision.Mode);
        Assert.Same(held, decision.IncompatibleHeldItem);
    }

    [Fact]
    public void GetDefaultCombatMode_NoWeaponDefaultsToUnarmedMelee()
    {
        Assert.Equal(
            CombatMode.Melee,
            CombatInputPlanner.GetDefaultCombatMode([]));
    }

    [Fact]
    public void ToggleMode_FromNonCombat_UsesDefaultCombatMode()
    {
        Assert.Equal(CombatMode.Melee, CombatInputPlanner.ToggleMode(CombatMode.NonCombat));
        Assert.Equal(
            CombatMode.Missile,
            CombatInputPlanner.ToggleMode(CombatMode.NonCombat, CombatMode.Missile));
        Assert.Equal(
            CombatMode.NonCombat,
            CombatInputPlanner.ToggleMode(CombatMode.NonCombat, CombatMode.NonCombat));
    }

    [Fact]
    public void ToggleMode_FromCombat_ReturnsNonCombat()
    {
        Assert.Equal(CombatMode.NonCombat, CombatInputPlanner.ToggleMode(CombatMode.Melee));
        Assert.Equal(CombatMode.NonCombat, CombatInputPlanner.ToggleMode(CombatMode.Magic));
        Assert.Equal(CombatMode.NonCombat, CombatInputPlanner.ToggleMode(CombatMode.Undef));
    }

    [Theory]
    [InlineData(CombatAttackAction.Low, AttackHeight.Low)]
    [InlineData(CombatAttackAction.Medium, AttackHeight.Medium)]
    [InlineData(CombatAttackAction.High, AttackHeight.High)]
    public void HeightFor_MapsRetailAttackKeys(CombatAttackAction action, AttackHeight expected)
    {
        Assert.Equal(expected, CombatInputPlanner.HeightFor(action));
    }

    [Theory]
    [InlineData(CombatMode.Melee, true)]
    [InlineData(CombatMode.Missile, true)]
    [InlineData(CombatMode.NonCombat, false)]
    [InlineData(CombatMode.Magic, false)]
    public void SupportsTargetedAttack_MatchesRetailExecuteAttackModes(
        CombatMode mode,
        bool expected)
    {
        Assert.Equal(expected, CombatInputPlanner.SupportsTargetedAttack(mode));
    }

    /// <summary>
    /// The whole ready-position rule, arm by arm. Peace and magic are ready
    /// once no motion is queued. Melee needs the combat table, then is ready
    /// at once to an attack builder (lenient) and once the motions have run
    /// out to a mode change (strict). Missile needs a bow-family stance held
    /// at the Ready command, with the same split.
    /// </summary>
    [Theory]
    [InlineData(CombatMode.Magic, 0x8000003Du, 0u, true, false, false, true)]
    [InlineData(CombatMode.Magic, 0x8000003Du, 0u, true, true, false, false)]
    [InlineData(CombatMode.Magic, 0x8000003Du, 0u, true, true, true, false)]
    [InlineData(CombatMode.NonCombat, 0x8000003Du, 0u, true, false, false, true)]
    [InlineData(CombatMode.NonCombat, 0x8000003Du, 0u, true, true, true, false)]
    [InlineData(CombatMode.Melee, 0x8000003Du, 0u, false, false, true, false)]
    [InlineData(CombatMode.Melee, 0x8000003Du, 0u, true, true, true, true)]
    [InlineData(CombatMode.Melee, 0x8000003Du, 0u, true, true, false, false)]
    [InlineData(CombatMode.Melee, 0x8000003Du, 0u, true, false, false, true)]
    [InlineData(CombatMode.Missile, 0x8000003Fu, CombatInputPlanner.ReadyForwardCommand, true, true, true, true)]
    [InlineData(CombatMode.Missile, 0x8000003Fu, CombatInputPlanner.ReadyForwardCommand, true, true, false, false)]
    [InlineData(CombatMode.Missile, 0x8000003Fu, CombatInputPlanner.ReadyForwardCommand, true, false, false, true)]
    [InlineData(CombatMode.Missile, 0x8000003Du, CombatInputPlanner.ReadyForwardCommand, true, false, true, false)]
    [InlineData(CombatMode.Missile, 0x8000003Fu, 0x41000006u, true, false, true, false)]
    public void PlayerInReadyPosition_MatchesEveryArmOfTheRetailRule(
        CombatMode mode,
        uint style,
        uint forward,
        bool hasCombatTable,
        bool motionsPending,
        bool lenient,
        bool expected)
    {
        Assert.Equal(
            expected,
            CombatInputPlanner.PlayerInReadyPosition(
                mode, style, forward, hasCombatTable, motionsPending, lenient));
    }

    [Theory]
    [InlineData(CombatMode.Melee, 0x8000003Du, 0u, true)]
    [InlineData(CombatMode.Missile, 0x8000003Fu, CombatInputPlanner.ReadyForwardCommand, true)]
    [InlineData(CombatMode.Missile, 0x80000041u, CombatInputPlanner.ReadyForwardCommand, true)]
    [InlineData(CombatMode.Missile, 0x8000003Du, CombatInputPlanner.ReadyForwardCommand, false)]
    [InlineData(CombatMode.Missile, 0x8000003Fu, 0x41000006u, false)]
    [InlineData(CombatMode.NonCombat, 0x8000003Du, CombatInputPlanner.ReadyForwardCommand, false)]
    public void PlayerInReadyPositionForAttack_MatchesRetailAttackBranch(
        CombatMode mode,
        uint style,
        uint forward,
        bool expected)
    {
        Assert.Equal(
            expected,
            CombatInputPlanner.PlayerInReadyPositionForAttack(mode, style, forward));
    }

    private static ClientObject Equipped(
        EquipMask location,
        ItemType type,
        byte combatUse) => new()
    {
        ObjectId = 1,
        CurrentlyEquippedLocation = location,
        Type = type,
        CombatUse = combatUse,
    };
}
