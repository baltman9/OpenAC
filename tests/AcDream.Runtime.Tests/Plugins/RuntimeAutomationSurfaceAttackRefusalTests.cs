using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// A plugin that is told only "refused" cannot tell whether the monster is
/// the problem.
/// </summary>
public sealed class RuntimeAutomationSurfaceAttackRefusalTests
{
    private const uint Player = 0x50000001u;
    private const uint Monster = 0x50000010u;
    private const uint Other = 0x50000011u;

    /// <summary>
    /// A refusal a plugin can act on. Without a reason a caller can only
    /// guess whether the monster is the problem, and writes off one that is
    /// standing right next to the character. Each branch names a different
    /// cause, and none of them is empty.
    /// Mutation: collapse the three branches to one string and this fails.
    /// </summary>
    [Fact]
    public void ARefusedPluginAttackSaysWhy()
    {
        using GameRuntime runtime = Create();
        RuntimeEntityTestSpawns.Add(
            runtime, Other, 11f, 10f, RuntimeEntityTestSpawns.Monster(Other));
        RuntimeEntityTestSpawns.Add(
            runtime,
            Monster,
            12f,
            10f,
            RuntimeEntityTestSpawns.Monster(Monster),
            PhysicsStateFlags.Hidden);

        runtime.ActionOwner.Selection.Select(Other, SelectionChangeSource.Plugin);
        string lost = RuntimeAutomationSurface.DescribeAttackRefusal(runtime, Monster);

        runtime.ActionOwner.Selection.Select(Monster, SelectionChangeSource.Plugin);
        string unattackable =
            RuntimeAutomationSurface.DescribeAttackRefusal(runtime, Monster);

        runtime.ActionOwner.Selection.Select(Other, SelectionChangeSource.Plugin);
        string notReady = RuntimeAutomationSurface.DescribeAttackRefusal(runtime, Other);

        Assert.False(string.IsNullOrWhiteSpace(lost));
        Assert.Equal(3, new HashSet<string>([lost, unattackable, notReady]).Count);
    }


    private static GameRuntime Create()
    {
        GameRuntime runtime = GameRuntimeTestFactory.Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityTestSpawns.Add(
            runtime,
            Player,
            10f,
            10f,
            RuntimeEntityTestSpawns.PlayerObject(Player));
        return runtime;
    }
}
