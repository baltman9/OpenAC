using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.Entities;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The little world a scenario stages, written once and handed to both arms
/// so the two clients are looking at exactly the same thing when they are
/// asked to do something.
/// </summary>
internal static class ParityWorld
{
    internal const uint Player = 0x50000001u;
    internal const uint Monster = 0x50000012u;
    internal const uint SecondMonster = 0x50000013u;
    internal const uint DeadMonster = 0x50000011u;
    internal const uint HiddenMonster = 0x50000010u;
    internal const uint Bystander = 0x50000020u;

    /// <summary>
    /// The player, a monster two metres further out, a second monster beyond
    /// it, a dead one, a hidden one and a harmless bystander.
    /// </summary>
    internal static void Stage(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, 10f, PlayerObject(Player));
        Add(runtime, HiddenMonster, 11f, MonsterObject(HiddenMonster),
            PhysicsStateFlags.Hidden);
        Add(runtime, DeadMonster, 12f, MonsterObject(DeadMonster));
        runtime.ActionOwner.Combat.OnUpdateHealth(DeadMonster, 0f);
        Add(runtime, Monster, 13f, MonsterObject(Monster));
        Add(runtime, SecondMonster, 17f, MonsterObject(SecondMonster));
        Add(runtime, Bystander, 14f, BystanderObject(Bystander));
    }

    internal static ClientObject PlayerObject(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        Name = "Parity",
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer,
    };

    internal static ClientObject MonsterObject(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        Name = $"Monster {objectId:X8}",
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfAttackable,
    };

    /// <summary>A creature nothing may swing at.</summary>
    internal static ClientObject BystanderObject(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        Name = $"Bystander {objectId:X8}",
        PublicWeenieBitfield = 0u,
    };

    internal static void Add(
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
            0x01010001u, x, 10f, 5f, 1f, 0f, 0f, 0f);
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
