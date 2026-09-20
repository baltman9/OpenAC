using AcDream.Core.Combat;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The pace of a plugin-driven fight, through the one attack state both hosts
/// bind. Nothing here is host-specific, which is the point: the window and the
/// windowless host share this state and differ only in the transport behind
/// <c>IRuntimeCombatAttackOperations</c>, so what is proved here is what both
/// of them do.
/// </summary>
public sealed class RuntimeAutomationSurfaceCombatPaceTests
{
    private const uint Player = 0x50000001u;
    private const uint MonsterA = 0x50000010u;
    private const uint MonsterB = 0x50000011u;

    /// <summary>
    /// Mutation: refuse every request while a swing is open, as a plain busy
    /// check does, and the request for the creature still alive answers Busy -
    /// the character stands in front of the one it has killed until the server
    /// gets round to answering for that swing.
    /// </summary>
    [Fact]
    public void ARequestAtAnotherCreatureEndsTheOpenSwingInsteadOfWaitingOnIt()
    {
        using var host = new NoWindowGameRuntimeHost();
        host.Start();
        for (int i = 0; i < 4; i++)
            host.Session.Tick();
        GameRuntime runtime = host.Runtime;
        Assert.True(runtime.Session.IsInWorld);

        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityTestSpawns.Add(
            runtime, Player, 10f, 10f, RuntimeEntityTestSpawns.PlayerObject(Player));
        RuntimeEntityTestSpawns.Add(
            runtime, MonsterA, 11f, 10f, RuntimeEntityTestSpawns.Monster(MonsterA));
        RuntimeEntityTestSpawns.Add(
            runtime, MonsterB, 12f, 10f, RuntimeEntityTestSpawns.Monster(MonsterB));
        runtime.ActionOwner.Combat.SetCombatMode(CombatMode.Melee);

        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        using IDisposable? control = surface.AcquireCombatControl();
        Assert.NotNull(control);

        Assert.Equal(
            PluginCombatCommandStatus.Started,
            surface.Combat.BeginPhysicalAttack(
                MonsterA, PluginAttackHeight.High, 1f).Status);

        // A second request at the same creature is the ordinary pace of a
        // fight, and still waits.
        Assert.Equal(
            PluginCombatCommandStatus.Busy,
            surface.Combat.BeginPhysicalAttack(
                MonsterA, PluginAttackHeight.High, 1f).Status);

        // That creature is dead and the macro has moved on: this one goes
        // through at once, and the swing now belongs to the new creature.
        Assert.Equal(
            PluginCombatCommandStatus.Started,
            surface.Combat.BeginPhysicalAttack(
                MonsterB, PluginAttackHeight.High, 1f).Status);
        Assert.Equal(MonsterB, runtime.ActionOwner.Selection.SelectedObjectId);
    }
}
