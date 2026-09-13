using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Tests;

/// <summary>
/// The smallest live entity a gameplay query can be asked about: an accepted
/// spawn at a landblock position plus the client object behind it.
/// </summary>
internal static class RuntimeEntityTestSpawns
{
    public static ClientObject PlayerObject(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer,
    };

    public static ClientObject Monster(uint objectId) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfAttackable,
    };

    public static void Add(
        GameRuntime runtime,
        uint guid,
        float x,
        float y,
        ClientObject item,
        PhysicsStateFlags state = 0,
        uint landblock = 0x01010001u,
        float z = 5f)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(guid, landblock, x, y, state, z))
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
        uint landblock,
        float x,
        float y,
        PhysicsStateFlags state,
        float z)
    {
        var position = new CreateObject.ServerPosition(
            landblock,
            x,
            y,
            z,
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
