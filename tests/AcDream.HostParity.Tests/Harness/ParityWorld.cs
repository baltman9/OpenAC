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
///
/// Everything stands on the one flat square of ground <see cref="ParityPlayerBody"/>
/// lays down, a metre apart along the x axis, so "the nearest creature" and
/// "how far away is it" are questions with real answers.
/// </summary>
internal static class ParityWorld
{
    internal const uint Player = 0x50000001u;
    internal const uint Monster = 0x50000012u;
    internal const uint SecondMonster = 0x50000013u;
    internal const uint DeadMonster = 0x50000011u;
    internal const uint HiddenMonster = 0x50000010u;
    internal const uint Bystander = 0x50000020u;

    /// <summary>Where the character stands, in metres inside its cell.</summary>
    internal const float PlayerX = 96f;
    internal const float PlayerY = 97f;

    /// <summary>
    /// The player, a monster two metres further out, a second monster beyond
    /// it, a dead one, a hidden one and a harmless bystander -- and, with a
    /// body, a character that can turn towards any of them and swing.
    /// </summary>
    internal static ParityPlayerBody Stage(ParityArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);
        GameRuntime runtime = arm.Runtime;
        ParityPlayerBody body = arm.AdoptPlayerBody(
            ParityPlayerBody.Prepare(runtime));
        _ = body.SpawnLocalPlayer(Player, PlayerX, PlayerY);
        runtime.InventoryOwner.Objects.AddOrUpdate(PlayerObject(Player));

        Add(runtime, HiddenMonster, PlayerX + 1f, MonsterObject(HiddenMonster),
            PhysicsStateFlags.Hidden);
        Add(runtime, DeadMonster, PlayerX + 2f, MonsterObject(DeadMonster));
        runtime.ActionOwner.Combat.OnUpdateHealth(DeadMonster, 0f);
        Add(runtime, Monster, PlayerX + 3f, MonsterObject(Monster));
        Add(runtime, SecondMonster, PlayerX + 7f, MonsterObject(SecondMonster));
        Add(runtime, Bystander, PlayerX + 4f, BystanderObject(Bystander));
        body.Drive();
        return body;
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

    // ── things to carry, and things to open ─────────────────────────────

    /// <summary>A healing kit: usable on its own, carried in the main pack.</summary>
    internal const uint Kit = 0x50000040u;

    /// <summary>A side pack, carried, with room for four things.</summary>
    internal const uint SidePack = 0x50000041u;

    /// <summary>A tinkering tool, which is what a salvage request needs.</summary>
    internal const uint SalvageTool = 0x50000042u;

    /// <summary>Something to salvage with it.</summary>
    internal const uint ScrapItem = 0x50000043u;

    /// <summary>An item that cannot be used without naming a target.</summary>
    internal const uint TargetedItem = 0x50000044u;

    /// <summary>A corpse lying three metres out, openable.</summary>
    internal const uint Corpse = 0x50000050u;

    /// <summary>Inside the corpse: something plain, and something worth a look.</summary>
    internal const uint CorpseCoin = 0x50000051u;
    internal const uint CorpseGem = 0x50000052u;

    /// <summary>
    /// Fills the character's packs: a kit to use, a side pack to move things
    /// into, a tinkering tool and something to salvage with it, and an item
    /// that refuses to be used without a target. The player object itself is
    /// given room, because where a picked-up item goes is decided by the
    /// client, not the server, and a container with no room decides
    /// differently.
    /// </summary>
    internal static void StageCarriedItems(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        ClientObject player = PlayerObject(Player);
        player.ItemsCapacity = 10;
        player.ContainersCapacity = 2;
        objects.AddOrUpdate(player);
        objects.AddOrUpdate(Carried(Kit, "Healing Kit", ItemUseability.Contained));
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = SidePack,
            Type = ItemType.Container,
            Name = "Side Pack",
            ContainerId = Player,
            WeenieClassId = 0x0000_0834u,
            ItemsCapacity = 4,
            ContainerTypeHint = 1,
            StackSize = 1,
            Useability = ItemUseability.Contained,
        });
        ClientObject tool = Carried(
            SalvageTool, "Tinkering Tool", ItemUseability.Contained);
        tool.Type = ItemType.TinkeringTool;
        objects.AddOrUpdate(tool);
        objects.AddOrUpdate(Carried(ScrapItem, "Scrap", ItemUseability.Contained));
        objects.AddOrUpdate(Carried(
            TargetedItem,
            "Mana Stone",
            ItemUseability.Contained | (ItemUseability.Contained << 16)));
    }

    /// <summary>
    /// Lays a corpse on the ground three metres out with nothing visible in
    /// it yet, the way one looks before it has been opened. Its contents
    /// arrive from <see cref="DeliverCorpseContents"/>.
    /// </summary>
    internal static void StageCorpse(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Add(runtime, Corpse, PlayerX + 3f, new ClientObject
        {
            ObjectId = Corpse,
            Type = ItemType.Container,
            Name = "Corpse of Monster",
            ItemsCapacity = 8,
            StackSize = 1,
            Useability = ItemUseability.Remote,
            PublicWeenieBitfield =
                (uint)(PublicWeenieFlags.Corpse | PublicWeenieFlags.Openable),
        });
    }

    /// <summary>
    /// What the server says once the corpse has been used: the two things
    /// inside it, and the answer that the listing is complete.
    /// </summary>
    internal static void DeliverCorpseContents(GameRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = CorpseCoin,
            Type = ItemType.Money,
            Name = "Pyreal",
            ContainerId = Corpse,
            WeenieClassId = 0x0000_0111u,
            StackSize = 250,
            StackSizeMax = 25000,
            Useability = ItemUseability.Contained,
        });
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = CorpseGem,
            Type = ItemType.Gem,
            Name = "Gem",
            ContainerId = Corpse,
            WeenieClassId = 0x0000_0222u,
            StackSize = 1,
            Useability = ItemUseability.Contained,
        });
        // The listing the server sends with the contents, in its order.
        objects.ReplaceContents(Corpse, [CorpseCoin, CorpseGem]);
        _ = runtime.InventoryOwner.ExternalContainers.ApplyViewContents(Corpse);
    }

    private static ClientObject Carried(
        uint objectId, string name, uint useability) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Misc,
        Name = name,
        ContainerId = Player,
        WeenieClassId = objectId & 0xFFFFu,
        StackSize = 1,
        Useability = useability,
    };

    internal static void Add(
        GameRuntime runtime,
        uint guid,
        float x,
        ClientObject item,
        PhysicsStateFlags state = 0)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(guid, x, PlayerY, ParityPlayerBody.Cell, state))
            .Canonical!;
        runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false);
        runtime.InventoryOwner.Objects.AddOrUpdate(item);
    }


    internal static WorldSession.EntitySpawn Spawn(
        uint guid,
        float x,
        float y,
        uint cell,
        PhysicsStateFlags state)
    {
        var position = new CreateObject.ServerPosition(
            cell, x, y, ParityPlayerBody.GroundHeight, 1f, 0f, 0f, 0f);
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
