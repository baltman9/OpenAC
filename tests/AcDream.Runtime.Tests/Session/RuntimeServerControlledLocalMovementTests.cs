using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Session;

/// <summary>
/// What a window-less host does with a movement the server drives at the
/// local character. The server's answer to "use that" when the thing is out
/// of its reach is one of these, and a character that does not obey never
/// arrives, so the use it asked for silently does nothing.
/// </summary>
public sealed class RuntimeServerControlledLocalMovementTests
{
    private const uint LocalCell = 0xA9B40029u;
    private const uint TargetGuid = 0x80000DADu;

    private static CreateObject.MoveToPathData WalkPath(
        uint? targetGuid = TargetGuid) =>
        new(
            TargetGuid: targetGuid,
            OriginCellId: LocalCell,
            OriginX: 133.6f,
            OriginY: 22.4f,
            OriginZ: 94f,
            DistanceToObject: 1.8f,
            MinDistance: 0f,
            FailDistance: 0f,
            WalkRunThreshold: 15f,
            DesiredHeading: 0f,
            Bitfield: 0x403u);

    private static CreateObject.TurnToPathData FacePath(
        uint? targetGuid = TargetGuid) =>
        new(
            TargetGuid: targetGuid,
            WireHeading: 90f,
            Bitfield: 0x3u,
            Speed: 1f,
            DesiredHeading: 0f);

    private static WorldSession.EntityMotionUpdate Order(
        byte movementType,
        CreateObject.MoveToPathData? walk = null,
        CreateObject.TurnToPathData? face = null) =>
        new(
            Guid: 0x50000001u,
            MotionState: new CreateObject.ServerMotionState(
                Stance: (ushort)0x3Du,
                ForwardCommand: null,
                MovementType: movementType,
                MoveToSpeed: 1f,
                MoveToRunRate: 1.75f,
                MoveToPath: walk,
                TurnToPath: face),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 3,
            IsAutonomous: false);

    private static WorldSession.EntityMotionUpdate MoveToObjectOrder(
        uint targetGuid = TargetGuid) =>
        Order(6, walk: WalkPath(targetGuid));

    private static bool Resolve(
        WorldSession.EntityMotionUpdate update,
        out MovementStruct request,
        out float? runRate,
        float? targetRadius = 0.4f,
        float targetHeight = 1.3f,
        (float X, float Y)? frameOffset = null) =>
        RuntimeServerControlledLocalMovement.TryResolve(
            update,
            targetBody: _ => targetRadius is { } radius
                ? (radius, targetHeight)
                : null,
            worldFrameOffset: _ => frameOffset ?? (0f, 0f),
            localCellId: LocalCell,
            out request,
            out runRate);

    [Fact]
    public void AnOrderToWalkToSomethingWithABodyFollowsThatBody()
    {
        Assert.True(Resolve(
            MoveToObjectOrder(),
            out MovementStruct request,
            out float? runRate));

        Assert.Equal(MovementType.MoveToObject, request.Type);
        Assert.Equal(TargetGuid, request.ObjectId);
        Assert.Equal(TargetGuid, request.TopLevelId);
        Assert.Equal(0.4f, request.Radius);
        // How tall it is travels with how wide it is: the gap that ends the
        // walk is measured between two cylinders, and a thing with no height
        // is a flat disc on the floor.
        Assert.Equal(1.3f, request.Height);
        Assert.Equal(1.8f, request.Params!.DistanceToObject);
        Assert.Equal(1.75f, runRate);
    }

    [Fact]
    public void AnOrderNamingSomethingWithNoBodyWalksToThePlaceInstead()
    {
        Assert.True(Resolve(
            MoveToObjectOrder(),
            out MovementStruct request,
            out _,
            targetRadius: null,
            frameOffset: (192f, -192f)));

        Assert.Equal(MovementType.MoveToPosition, request.Type);
        Assert.Equal(LocalCell, request.Pos.ObjCellId);
        Assert.Equal(133.6f + 192f, request.Pos.Frame.Origin.X, 3);
        Assert.Equal(22.4f - 192f, request.Pos.Frame.Origin.Y, 3);
        Assert.Equal(94f, request.Pos.Frame.Origin.Z, 3);
        Assert.Equal(1.8f, request.Params!.DistanceToObject);
    }

    [Fact]
    public void TheKindOfMovementDecidesWhetherItIsAnOrderAtAll()
    {
        // Everything an order would need is present except the one thing that
        // says it IS one: an ordinary motion carries the same records and asks
        // for nothing.
        Assert.False(Resolve(
            Order(0, walk: WalkPath(), face: FacePath()),
            out _,
            out _));

        Assert.True(Resolve(
            Order(8, walk: WalkPath(), face: FacePath()),
            out MovementStruct faced,
            out _));
        Assert.Equal(MovementType.TurnToObject, faced.Type);

        Assert.True(Resolve(
            Order(6, walk: WalkPath(), face: FacePath()),
            out MovementStruct walked,
            out _));
        Assert.Equal(MovementType.MoveToObject, walked.Type);
    }

    [Fact]
    public void AnOrderThatNamesAPlaceWalksThereEvenWhenABodyIsAtHand()
    {
        // The kind that names no thing to follow: a body being available for
        // the id in the record must not turn it into a follow.
        Assert.True(Resolve(
            Order(7, walk: WalkPath()),
            out MovementStruct request,
            out _,
            frameOffset: (0f, 0f)));

        Assert.Equal(MovementType.MoveToPosition, request.Type);
        Assert.Equal(133.6f, request.Pos.Frame.Origin.X, 3);
    }

    [Fact]
    public void AnOrderWhoseFrameIsNotEstablishedYetStartsNothing()
    {
        Assert.False(RuntimeServerControlledLocalMovement.TryResolve(
            MoveToObjectOrder(),
            targetBody: _ => null,
            worldFrameOffset: _ => null,
            localCellId: LocalCell,
            out _,
            out _));
    }

    [Fact]
    public void AnOrderToFaceSomethingWithABodyTurnsToIt()
    {
        Assert.True(Resolve(
            Order(8, face: FacePath()),
            out MovementStruct request,
            out _));
        Assert.Equal(MovementType.TurnToObject, request.Type);
        Assert.Equal(TargetGuid, request.ObjectId);
    }

    [Fact]
    public void AnOrderToFaceSomethingWithNoBodyTurnsToTheHeadingItNames()
    {
        Assert.True(Resolve(
            Order(8, face: FacePath()),
            out MovementStruct request,
            out _,
            targetRadius: null));
        Assert.Equal(MovementType.TurnToHeading, request.Type);
        Assert.Equal(90f, request.Params!.DesiredHeading);
    }
}
