using AcDream.Core.Combat;

namespace AcDream.Core.Net.Tests;

public sealed class CombatStateWiringTests
{
    [Theory]
    [InlineData((int)CombatMode.NonCombat)]
    [InlineData((int)CombatMode.Melee)]
    [InlineData((int)CombatMode.Missile)]
    [InlineData((int)CombatMode.Magic)]
    public void CombatModeProperty_appliesConcreteRetailMode(int value)
    {
        var combat = new CombatState();

        Assert.True(CombatStateWiring.ApplyPlayerIntProperty(
            combat,
            CombatStateWiring.CombatModePropertyId,
            value));

        Assert.Equal((CombatMode)value, combat.CurrentMode);
        Assert.Equal((CombatMode)value, combat.ServerMode);
    }

    /// <summary>
    /// The server's word is kept apart from the client's own: a change the
    /// client makes on its own moves the current mode and leaves the server's
    /// last word where it was, until the server speaks again; before it has
    /// spoken there is no word at all, and clearing forgets it.
    /// </summary>
    [Fact]
    public void ServerModeIsTheServersLastWordNotTheClientsOwnChange()
    {
        var combat = new CombatState();
        Assert.Null(combat.ServerMode);

        combat.SetCombatMode(CombatMode.Magic);
        Assert.Equal(CombatMode.Magic, combat.CurrentMode);
        Assert.Null(combat.ServerMode);

        Assert.True(CombatStateWiring.ApplyPlayerIntProperty(
            combat, CombatStateWiring.CombatModePropertyId, (int)CombatMode.NonCombat));
        Assert.Equal(CombatMode.NonCombat, combat.CurrentMode);
        Assert.Equal(CombatMode.NonCombat, combat.ServerMode);

        combat.SetCombatMode(CombatMode.Melee);
        Assert.Equal(CombatMode.Melee, combat.CurrentMode);
        Assert.Equal(CombatMode.NonCombat, combat.ServerMode);

        combat.Clear();
        Assert.Null(combat.ServerMode);
    }

    [Theory]
    [InlineData(39u, (int)CombatMode.Missile)]
    [InlineData(40u, (int)CombatMode.Undef)]
    [InlineData(40u, (int)CombatMode.ValidCombat)]
    [InlineData(40u, 0x10)]
    public void NonCombatQualityOrInvalidValue_isIgnored(uint property, int value)
    {
        var combat = new CombatState();

        Assert.False(CombatStateWiring.ApplyPlayerIntProperty(
            combat, property, value));

        Assert.Equal(CombatMode.NonCombat, combat.CurrentMode);
    }
}
