using AcDream.App.Combat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Runtime;
using AcDream.Runtime.Entities;

namespace AcDream.App.Tests.Combat;

public sealed class CombatAttackTargetSourceTests
{
    private const uint Player = 0x5000_0001u;

    [Fact]
    public void ExplicitSelectedHostileIsAcceptedWithoutAutoTarget()
    {
        using var harness = new Harness();
        const uint target = 0x7000_0001u;
        harness.AddMonster(target, x: 12f);
        harness.Select(target);

        Assert.Equal(
            target,
            harness.Targets.GetSelectedOrClosestCombatTarget(allowAutoTarget: false));
    }

    [Fact]
    public void ExplicitSelectedPkLitePlayerIsAcceptedWithoutAutoTarget()
    {
        using var harness = new Harness();
        harness.SetLocalPlayerPvpFlags(SelectedObjectHealthPolicy.BfPkLiteStatus);
        const uint target = 0x7000_0002u;
        harness.Add(
            target,
            x: 12f,
            flags: SelectedObjectHealthPolicy.BfPlayer
                | SelectedObjectHealthPolicy.BfPkLiteStatus);
        harness.Select(target);

        Assert.Equal(
            target,
            harness.Targets.GetSelectedOrClosestCombatTarget(allowAutoTarget: false));
    }

    [Fact]
    public void ExplicitSelectedNonPkPlayerIsRefused()
    {
        using var harness = new Harness();
        const uint target = 0x7000_0003u;
        harness.Add(target, x: 12f, flags: SelectedObjectHealthPolicy.BfPlayer);
        harness.Select(target);

        Assert.Null(
            harness.Targets.GetSelectedOrClosestCombatTarget(allowAutoTarget: false));
    }

    [Fact]
    public void ExplicitSelectedAttackablePetIsRefused()
    {
        using var harness = new Harness();
        const uint pet = 0x7000_0007u;
        harness.Add(
            pet,
            x: 12f,
            flags: SelectedObjectHealthPolicy.BfAttackable,
            petOwnerId: Player);
        harness.Select(pet);

        Assert.Null(
            harness.Targets.GetSelectedOrClosestCombatTarget(allowAutoTarget: false));
    }

    [Fact]
    public void AutoTargetNeverAcquiresAPlayerOverAMonster()
    {
        using var harness = new Harness(autoTarget: true);
        harness.SetLocalPlayerPvpFlags(SelectedObjectHealthPolicy.BfPkLiteStatus);
        const uint monster = 0x7000_0004u;
        const uint pkLitePlayer = 0x7000_0005u;
        harness.AddMonster(monster, x: 18f);
        harness.Add(
            pkLitePlayer,
            x: 11f,
            flags: SelectedObjectHealthPolicy.BfPlayer
                | SelectedObjectHealthPolicy.BfPkLiteStatus);

        uint? selected = harness.Targets.GetSelectedOrClosestCombatTarget(
            allowAutoTarget: true);

        Assert.Equal(monster, selected);
        Assert.Equal(monster, harness.Runtime.ActionOwner.Selection.SelectedObjectId);
    }

    [Fact]
    public void AutoTargetNeverAcquiresAPlayerWhenNoMonsterIsInRange()
    {
        using var harness = new Harness(autoTarget: true);
        harness.SetLocalPlayerPvpFlags(SelectedObjectHealthPolicy.BfPkLiteStatus);
        harness.Add(
            0x7000_0006u,
            x: 11f,
            flags: SelectedObjectHealthPolicy.BfPlayer
                | SelectedObjectHealthPolicy.BfPkLiteStatus);

        Assert.Null(
            harness.Targets.GetSelectedOrClosestCombatTarget(allowAutoTarget: true));
        Assert.Null(harness.Runtime.ActionOwner.Selection.SelectedObjectId);
    }

    [Fact]
    public void AutoTargetUsesNearestEligibleLiveHostile()
    {
        using var harness = new Harness(autoTarget: true);
        const uint valid = 0x7000_0010u;
        harness.AddMonster(valid, x: 18f);
        harness.Add(0x7000_0011u, x: 12f, flags: 0u);
        const uint dead = 0x7000_0012u;
        harness.AddMonster(dead, x: 13f);
        harness.Runtime.ActionOwner.Combat.OnUpdateHealth(dead, 0f);
        harness.AddMonster(0x7000_0013u, x: 14f, state: PhysicsStateFlags.Hidden);
        harness.AddMonster(0x7000_0014u, x: 15f, state: PhysicsStateFlags.NoDraw);

        uint? selected = harness.Targets.GetSelectedOrClosestCombatTarget(
            allowAutoTarget: true);

        Assert.Equal(valid, selected);
        Assert.Equal(valid, harness.Runtime.ActionOwner.Selection.SelectedObjectId);
    }

    [Fact]
    public void MissingAutoTargetClearsAnInvalidSelection()
    {
        using var harness = new Harness(autoTarget: true);
        const uint nonHostile = 0x7000_0020u;
        harness.Add(nonHostile, x: 11f, flags: 0u);
        harness.Select(nonHostile);

        Assert.Null(
            harness.Targets.GetSelectedOrClosestCombatTarget(allowAutoTarget: true));
        Assert.Null(harness.Runtime.ActionOwner.Selection.SelectedObjectId);
    }

    /// <summary>
    /// The option is the player's, and it only ever adds the substitution: a
    /// selected monster is attacked whether the option is on or off.
    /// Mutation: make the closest-hostile fallback unconditional and the
    /// second case below acquires the monster instead of refusing.
    /// </summary>
    [Fact]
    public void WithoutTheAutoTargetOptionOnlyTheSelectionIsAttacked()
    {
        using var harness = new Harness();
        const uint monster = 0x7000_0030u;
        harness.AddMonster(monster, x: 12f);

        harness.Select(monster);
        Assert.Equal(
            monster,
            harness.Targets.GetSelectedOrClosestCombatTarget(allowAutoTarget: true));

        harness.Runtime.ActionOwner.Selection.Clear(SelectionChangeSource.Keyboard);
        Assert.Null(
            harness.Targets.GetSelectedOrClosestCombatTarget(allowAutoTarget: true));
    }

    /// <summary>
    /// The defect this pins: a monster the runtime calls attackable is
    /// attackable under a window too. A plugin names a target, the runtime
    /// selects it, and the graphical host must swing at that one and no
    /// other, whatever the selection held a moment before, and with the
    /// player's auto-target option off.
    /// Mutation: answer from anything other than the runtime's own
    /// attack-target owner and both cases below go null.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APluginNamedTargetIsTheOneAttacked(bool selectionHeldSomethingElse)
    {
        using var harness = new Harness();
        const uint corpse = 0x7000_0040u;
        const uint named = 0x7000_0041u;
        harness.Add(corpse, x: 11f, flags: 0u, type: ItemType.Container);
        harness.AddMonster(named, x: 13f);
        if (selectionHeldSomethingElse)
            harness.Select(corpse);

        // What the automation surface does on the plugin's behalf.
        harness.Runtime.ActionOwner.Selection.Select(
            named,
            SelectionChangeSource.Plugin);

        Assert.Equal(
            named,
            harness.Targets.GetSelectedOrClosestCombatTarget(allowAutoTarget: false));
    }

    /// <summary>
    /// Host parity: the same runtime, the same question, the same answer,
    /// because both hosts now ask one owner.
    /// Mutation: give the graphical host a rule of its own and this diverges.
    /// </summary>
    [Fact]
    public void BothHostsResolveTheSameTargetForTheSameCall()
    {
        using var harness = new Harness();
        const uint named = 0x7000_0050u;
        harness.AddMonster(named, x: 13f);
        harness.Runtime.ActionOwner.Selection.Select(
            named,
            SelectionChangeSource.Plugin);

        Assert.Equal(
            RuntimeAttackTargetResolver.Resolve(
                harness.Runtime,
                allowAutoTarget: false).Target,
            harness.Targets.GetSelectedOrClosestCombatTarget(allowAutoTarget: false));
    }

    private sealed class Harness : IDisposable
    {
        public GameRuntime Runtime { get; }
        public CombatAttackTargetSource Targets { get; }

        public Harness(bool autoTarget = false)
        {
            Runtime = GameRuntimeTestFactory.Create();
            Runtime.PlayerIdentity.ServerGuid = Player;
            Runtime.CharacterOwner.Options.SetOptionBit(
                (uint)CharacterOptionId.AutoTarget,
                autoTarget);
            Add(Player, x: 10f, flags: SelectedObjectHealthPolicy.BfPlayer);
            Targets = new CombatAttackTargetSource(Runtime);
        }

        public void Select(uint objectId) =>
            Runtime.ActionOwner.Selection.Select(
                objectId,
                SelectionChangeSource.Keyboard);

        public void SetLocalPlayerPvpFlags(uint extraFlags) =>
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Player,
                Name = $"Object {Player:X8}",
                Type = ItemType.Creature,
                PublicWeenieBitfield =
                    SelectedObjectHealthPolicy.BfPlayer | extraFlags,
            });

        public void AddMonster(
            uint guid,
            float x,
            PhysicsStateFlags state = 0) =>
            Add(guid, x, SelectedObjectHealthPolicy.BfAttackable, state: state);

        public void Add(
            uint guid,
            float x,
            uint flags,
            uint petOwnerId = 0u,
            ItemType type = ItemType.Creature,
            PhysicsStateFlags state = 0)
        {
            RuntimeEntityRecord record = Runtime.EntityObjects
                .RegisterEntity(Spawn(guid, x, state))
                .Canonical!;
            Runtime.EntityObjects.ApplyAcceptedSpawn(
                record,
                record.CreateIntegrationVersion,
                record.Snapshot,
                replaceGeneration: false);
            Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = guid,
                Name = $"Object {guid:X8}",
                Type = type,
                PublicWeenieBitfield = flags,
                PetOwnerId = petOwnerId,
            });
        }

        public void Dispose() => Runtime.Dispose();
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        float x,
        PhysicsStateFlags state)
    {
        var position = new CreateObject.ServerPosition(
            0x0101_0001u,
            x,
            10f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: 1);
        var physics = new PhysicsSpawnData(
            RawState: (uint)state,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x0200_0001u,
            MotionTableId: null,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x0200_0001u,
            [],
            [],
            [],
            null,
            null,
            guid.ToString("X8"),
            null,
            null,
            null,
            PhysicsState: physics.RawState,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
