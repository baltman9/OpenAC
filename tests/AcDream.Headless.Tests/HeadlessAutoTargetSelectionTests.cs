using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Headless.Hosting;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Tests;

public sealed class HeadlessAutoTargetSelectionTests
{
    private const uint Player = 0x50000001u;
    private const uint Hidden = 0x50000010u;
    private const uint Dead = 0x50000011u;
    private const uint Visible = 0x50000012u;

    /// <summary>
    /// A bot's auto-target picks what a player would pick.
    /// Mutation: ask the hostile query for the classified scope in
    /// <c>SelectClosestTarget</c> and the nearer hidden creature is selected.
    /// </summary>
    [Fact]
    public void SelectClosestTarget_SkipsHiddenAndDeadCreatures()
    {
        var gameplay = new HeadlessGameplayOperations();
        using var runtime = new GameRuntime(new GameRuntimeDependencies(
            gameplay,
            gameplay,
            gameplay,
            gameplay));
        gameplay.Bind(runtime, catalog: null, () => "account");
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 10f, PlayerObject(Player));
        Add(runtime, Hidden, 11f, Monster(Hidden), PhysicsStateFlags.Hidden);
        Add(runtime, Dead, 12f, Monster(Dead));
        runtime.ActionOwner.Combat.OnUpdateHealth(Dead, 0f);
        Add(runtime, Visible, 13f, Monster(Visible));

        uint? closest = gameplay.SelectClosestTarget();

        Assert.Equal(Visible, closest);
        Assert.Equal(
            Visible,
            runtime.ActionOwner.Selection.SelectedObjectId);
    }

    /// <summary>
    /// Host parity: the windowless host holds no target rule of its own, so
    /// its answer is the runtime owner's answer. A graphical session asks the
    /// same owner, which is what makes a plugin's named target the one that
    /// is swung at in either host.
    /// Mutation: give this host back a rule of its own and this diverges.
    /// </summary>
    [Fact]
    public void TheTargetRuleIsTheRuntimeOwnersInThisHostToo()
    {
        var gameplay = new HeadlessGameplayOperations();
        using var runtime = new GameRuntime(new GameRuntimeDependencies(
            gameplay,
            gameplay,
            gameplay,
            gameplay));
        gameplay.Bind(runtime, catalog: null, () => "account");
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 10f, PlayerObject(Player));
        Add(runtime, Visible, 13f, Monster(Visible));

        // What the automation surface does on a plugin's behalf.
        runtime.ActionOwner.Selection.Select(
            Visible,
            AcDream.Core.Selection.SelectionChangeSource.Plugin);

        Assert.Equal(
            RuntimeAttackTargetResolver
                .Resolve(runtime, allowAutoTarget: false)
                .Target,
            runtime.ActionOwner.Selection.SelectedObjectId);
        Assert.Equal(
            RuntimeAttackTargetResolver.SelectClosest(runtime),
            gameplay.SelectClosestTarget());
    }

    private static ClientObject PlayerObject(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer,
    };

    private static ClientObject Monster(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfAttackable,
    };

    private static void Add(
        GameRuntime runtime,
        uint guid,
        float x,
        ClientObject item,
        PhysicsStateFlags state = 0)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(guid, x, state))
            .Canonical!;
        runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false);
        runtime.InventoryOwner.Objects.AddOrUpdate(item);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        float x,
        PhysicsStateFlags state)
    {
        var position = new CreateObject.ServerPosition(
            0x01010001u,
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
            SetupTableId: 0x02000001u,
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
            0x02000001u,
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
