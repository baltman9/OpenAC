using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Tests.Plugins;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// One owner answers "which creature does this attack go to" for every host.
/// </summary>
public sealed class RuntimeAttackTargetResolverTests
{
    private const uint Player = 0x50000001u;
    private const uint Monster = 0x50000010u;
    private const uint Other = 0x50000011u;

    /// <summary>
    /// The case the graphical client got wrong: a target something else named
    /// and selected is the target, with no substitution allowed and no second
    /// opinion about whether it may be attacked.
    /// Mutation: resolve from anything but the current selection and this
    /// returns the stale object.
    /// </summary>
    [Fact]
    public void ANamedSelectionIsTheTargetEvenWithoutAutoTarget()
    {
        using GameRuntime runtime = Create();
        RuntimeEntityTestSpawns.Add(
            runtime, Other, 11f, 10f, RuntimeEntityTestSpawns.Monster(Other));
        RuntimeEntityTestSpawns.Add(
            runtime, Monster, 13f, 10f, RuntimeEntityTestSpawns.Monster(Monster));
        runtime.ActionOwner.Selection.Select(Other, SelectionChangeSource.World);

        runtime.ActionOwner.Selection.Select(Monster, SelectionChangeSource.Plugin);

        RuntimeAttackTargetResolution resolution =
            RuntimeAttackTargetResolver.Resolve(runtime, allowAutoTarget: false);
        Assert.Equal(Monster, resolution.Target);
        Assert.Equal(RuntimeAttackTargetRefusal.None, resolution.Refusal);
    }

    /// <summary>
    /// Mutation: drop the option check and the empty selection is filled with
    /// the nearest monster although the player asked for no such thing.
    /// </summary>
    [Fact]
    public void WithoutTheOptionAnEmptySelectionStaysEmpty()
    {
        using GameRuntime runtime = Create();
        RuntimeEntityTestSpawns.Add(
            runtime, Monster, 11f, 10f, RuntimeEntityTestSpawns.Monster(Monster));

        RuntimeAttackTargetResolution resolution =
            RuntimeAttackTargetResolver.Resolve(runtime, allowAutoTarget: true);

        Assert.Null(resolution.Target);
        Assert.Equal(RuntimeAttackTargetRefusal.NothingSelected, resolution.Refusal);
        Assert.Null(runtime.ActionOwner.Selection.SelectedObjectId);
    }

    /// <summary>
    /// Mutation: substitute regardless of <c>allowAutoTarget</c> and an
    /// automation-owned attack is handed a monster it never named.
    /// </summary>
    [Fact]
    public void AutoTargetFillsAnEmptySelectionOnlyWhenTheCallerAllowsIt()
    {
        using GameRuntime runtime = Create(autoTarget: true);
        RuntimeEntityTestSpawns.Add(
            runtime, Monster, 11f, 10f, RuntimeEntityTestSpawns.Monster(Monster));

        Assert.Null(
            RuntimeAttackTargetResolver.Resolve(runtime, allowAutoTarget: false).Target);
        Assert.Equal(
            Monster,
            RuntimeAttackTargetResolver.Resolve(runtime, allowAutoTarget: true).Target);
        Assert.Equal(Monster, runtime.ActionOwner.Selection.SelectedObjectId);
    }

    /// <summary>
    /// Mutation: judge an explicit selection with the monster scan instead of
    /// the selected-object rule and the player loses the one target they are
    /// entitled to attack that is not a monster.
    /// </summary>
    [Fact]
    public void ASharedPlayerKillerStatusIsAttackableWhenSelectedAndNeverAcquired()
    {
        using GameRuntime runtime = Create(autoTarget: true);
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Player,
            Type = ItemType.Creature,
            PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer
                | SelectedObjectHealthPolicy.BfPkLiteStatus,
        });
        RuntimeEntityTestSpawns.Add(
            runtime,
            Other,
            11f,
            10f,
            new ClientObject
            {
                ObjectId = Other,
                Type = ItemType.Creature,
                PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer
                    | SelectedObjectHealthPolicy.BfPkLiteStatus,
            });

        runtime.ActionOwner.Selection.Select(Other, SelectionChangeSource.World);
        Assert.Equal(
            Other,
            RuntimeAttackTargetResolver.Resolve(runtime, allowAutoTarget: true).Target);

        runtime.ActionOwner.Selection.Clear(SelectionChangeSource.World);
        RuntimeAttackTargetResolution acquired =
            RuntimeAttackTargetResolver.Resolve(runtime, allowAutoTarget: true);
        Assert.Null(acquired.Target);
        Assert.Equal(RuntimeAttackTargetRefusal.NoMonsterFound, acquired.Refusal);
    }

    /// <summary>
    /// Mutation: keep hidden or dead creatures in the selectable scope and a
    /// substitution locks onto something nobody can hit.
    /// </summary>
    [Fact]
    public void ASelectionThatHasLeftPlayIsRefused()
    {
        using GameRuntime runtime = Create();
        RuntimeEntityTestSpawns.Add(
            runtime,
            Monster,
            11f,
            10f,
            RuntimeEntityTestSpawns.Monster(Monster),
            PhysicsStateFlags.Hidden);
        runtime.ActionOwner.Selection.Select(Monster, SelectionChangeSource.World);

        RuntimeAttackTargetResolution resolution =
            RuntimeAttackTargetResolver.Resolve(runtime, allowAutoTarget: false);

        Assert.Null(resolution.Target);
        Assert.Equal(
            RuntimeAttackTargetRefusal.SelectionNotAttackable,
            resolution.Refusal);
    }

    private static GameRuntime Create(bool autoTarget = false)
    {
        GameRuntime runtime = GameRuntimeTestFactory.Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        runtime.CharacterOwner.Options.SetOptionBit(
            (uint)CharacterOptionId.AutoTarget,
            autoTarget);
        RuntimeEntityTestSpawns.Add(
            runtime,
            Player,
            10f,
            10f,
            RuntimeEntityTestSpawns.PlayerObject(Player));
        return runtime;
    }
}
