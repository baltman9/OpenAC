using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// Where a plugin is told an object is. The client keeps two answers about
/// that: the last thing the server said, and the body that moves between
/// those updates. Everything else the client does -- walking to a creature,
/// judging whether it is in reach, aiming at it -- works from the body, so
/// the list of objects a plugin reads has to as well. Two answers on one
/// surface send a bot to where a creature was rather than where it is.
///
/// Mutation check (2026-09-20), run: reading the list off the wire snapshot
/// again turned <see cref="AnObjectWithABodyIsReportedWhereTheBodyIs"/> red
/// and left <see cref="AnObjectWithNoBodyIsReportedWhereTheServerSaid"/>
/// green.
/// </summary>
public sealed class RuntimeWorldEntityProjectionPositionTests
{
    private const uint Monster = 0x50000020u;
    private const uint Landblock = 0x01010001u;

    [Fact]
    public void AnObjectWithNoBodyIsReportedWhereTheServerSaid()
    {
        using var host = new NoWindowGameRuntimeHost();
        Stage(host.Runtime);
        using var projection = new RuntimeWorldEntityProjection(host.Runtime);

        WorldEntitySnapshot only = Assert.Single(projection.Entities);

        Assert.Equal(new Vector3(10f, 20f, 5f), only.Position);
    }

    [Fact]
    public void AnObjectWithABodyIsReportedWhereTheBodyIs()
    {
        using var host = new NoWindowGameRuntimeHost();
        RuntimeEntityRecord record = Stage(host.Runtime);
        GiveItABodyThatHasMoved(host.Runtime, record);
        using var projection = new RuntimeWorldEntityProjection(host.Runtime);

        WorldEntitySnapshot only = Assert.Single(projection.Entities);

        Assert.Equal(new Vector3(14f, 20f, 5f), only.Position);
    }

    private static RuntimeEntityRecord Stage(GameRuntime runtime)
    {
        RuntimeEntityTestSpawns.Add(
            runtime,
            Monster,
            10f,
            20f,
            RuntimeEntityTestSpawns.Monster(Monster));
        Assert.True(runtime.EntityObjects.Entities.TryGetActive(
            Monster, out RuntimeEntityRecord record));
        return record;
    }

    /// <summary>
    /// The creature has walked four metres east since the server last said
    /// where it was, which is the ordinary state of a creature in motion.
    /// </summary>
    private static void GiveItABodyThatHasMoved(
        GameRuntime runtime,
        RuntimeEntityRecord record)
    {
        var body = new PhysicsBody
        {
            Position = new Vector3(14f, 20f, 5f),
            Orientation = Quaternion.Identity,
            LastUpdateTime = 1d,
            State = record.FinalPhysicsState,
            TransientState = TransientStateFlags.Active,
        };
        body.SnapToCell(Landblock, body.Position, body.Position);
        runtime.EntityObjects.Entities.SetPhysicsBody(record, body);
    }
}
