using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeWorldObjectHeadingTests
{
    /// <summary>
    /// A body that turns after it was placed reads to a plugin as facing its
    /// new way: a meta that compares the player's heading with the heading to
    /// a portal read 0 for the player whichever way it faced.
    /// Mutation: read the cell frame's orientation and the heading stays at
    /// the placement's.
    /// </summary>
    [Fact]
    public void ABodyThatTurnedAfterItWasPlacedReportsItsNewHeading()
    {
        var body = new PhysicsBody();
        body.SnapToCell(0xA9B40001u, new Vector3(10f, 20f, 30f), new Vector3(10f, 20f, 30f));
        Quaternion turned = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 3f);
        body.Orientation = turned;

        Position live = RuntimeWorldObjectProjection.LivePosition(body);
        float heading = RuntimeWorldObjectProjection.ProjectNavigationPosition(live).HeadingDegrees;

        Assert.Equal(MoveToMath.GetHeading(turned), heading, 3);
        Assert.NotEqual(
            MoveToMath.GetHeading(body.CellPosition.Frame.Orientation),
            heading,
            3);
        Assert.Equal(body.CellPosition.ObjCellId, live.ObjCellId);
        Assert.Equal(body.CellPosition.Frame.Origin, live.Frame.Origin);
    }
}
