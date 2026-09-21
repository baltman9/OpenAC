using System.Numerics;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeEntityBodyPlacementTests
{
    private const uint Guid = 0x70000101u;
    private const uint Cell = 0x01010001u;

    /// <summary>
    /// Mutation pin: answer with the body whether or not it has been settled.
    /// Every surface that asks then puts an unsettled thing at the origin of
    /// the world instead of where the server last said it was.
    /// </summary>
    [Fact]
    public void ABodyThatWasNeverSettledIsNotAnAnswer()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = Register(lifetime);
        var body = new PhysicsBody
        {
            Position = new Vector3(4f, 5f, 6f),
            Orientation = Quaternion.Identity,
        };
        Assert.Equal(0u, body.CellPosition.ObjCellId);
        lifetime.Entities.SetPhysicsBody(record, body);

        Assert.Null(RuntimeEntityBodyPlacement.SettledPosition(record));

        body.SnapToCell(Cell, body.Position, body.Position);
        lifetime.Entities.SetPhysicsBody(record, body);

        Position settled = Assert.IsType<Position>(
            RuntimeEntityBodyPlacement.SettledPosition(record));
        Assert.Equal(Cell, settled.ObjCellId);
        Assert.Equal(new Vector3(4f, 5f, 6f), settled.Frame.Origin);
    }

    /// <summary>A thing with no body of its own has no settled answer either.</summary>
    [Fact]
    public void AThingWithNoBodyIsNotAnAnswer()
    {
        using var lifetime = new RuntimeEntityObjectLifetime();
        RuntimeEntityRecord record = Register(lifetime);

        Assert.Null(RuntimeEntityBodyPlacement.SettledPosition(record));
    }

    private static RuntimeEntityRecord Register(
        RuntimeEntityObjectLifetime lifetime)
    {
        lifetime.BindEventContext(
            static () => new RuntimeGenerationToken(1UL),
            static () => 1UL);
        var position = new CreateObject.ServerPosition(
            Cell, 10f, 11f, 12f, 1f, 0f, 0f, 0f);
        var spawn = new WorldSession.EntitySpawn(
            Guid,
            position,
            null,
            [],
            [],
            [],
            null,
            null,
            "a thing",
            null,
            null,
            null,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: null);
        RuntimeEntityRecord record = lifetime.RegisterEntity(spawn).Canonical
            ?? throw new InvalidOperationException("refused");
        Assert.True(lifetime.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        return record;
    }
}
