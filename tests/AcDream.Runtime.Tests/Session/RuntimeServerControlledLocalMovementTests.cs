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

    private static WorldSession.EntityMotionUpdate MoveToObjectOrder(
        uint targetGuid = TargetGuid,
        bool autonomous = false) =>
        new(
            Guid: 0x50000001u,
            MotionState: new CreateObject.ServerMotionState(
                Stance: (ushort)0x3Du,
                ForwardCommand: null,
                MovementType: 6,
                MoveToSpeed: 1f,
                MoveToRunRate: 1.75f,
                MoveToPath: new CreateObject.MoveToPathData(
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
                    Bitfield: 0x403u)),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 3,
            IsAutonomous: autonomous);

    private static bool Resolve(
        WorldSession.EntityMotionUpdate update,
        out MovementStruct request,
        out float? runRate,
        float? targetRadius = 0.4f,
        (float X, float Y)? frameOffset = null) =>
        RuntimeServerControlledLocalMovement.TryResolve(
            update,
            targetBodyRadius: _ => targetRadius,
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
    public void TheCharactersOwnEchoedMovementIsNotAnOrder()
    {
        Assert.False(Resolve(
            MoveToObjectOrder(autonomous: true),
            out _,
            out _));
    }

    [Fact]
    public void AnOrdinaryMotionIsNotAnOrderToGoAnywhere()
    {
        var update = new WorldSession.EntityMotionUpdate(
            Guid: 0x50000001u,
            MotionState: new CreateObject.ServerMotionState(
                Stance: (ushort)0x3Du,
                ForwardCommand: (ushort)0x45,
                MovementType: 0),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 3,
            IsAutonomous: false);

        Assert.False(Resolve(update, out _, out _));
    }

    [Fact]
    public void AnOrderWhoseFrameIsNotEstablishedYetStartsNothing()
    {
        Assert.False(RuntimeServerControlledLocalMovement.TryResolve(
            MoveToObjectOrder(),
            targetBodyRadius: _ => null,
            worldFrameOffset: _ => null,
            localCellId: LocalCell,
            out _,
            out _));
    }

    [Fact]
    public void AnOrderToFaceSomethingWithABodyTurnsToIt()
    {
        var update = new WorldSession.EntityMotionUpdate(
            Guid: 0x50000001u,
            MotionState: new CreateObject.ServerMotionState(
                Stance: (ushort)0x3Du,
                ForwardCommand: null,
                MovementType: 8,
                TurnToPath: new CreateObject.TurnToPathData(
                    TargetGuid: TargetGuid,
                    WireHeading: 90f,
                    Bitfield: 0x3u,
                    Speed: 1f,
                    DesiredHeading: 0f)),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 3,
            IsAutonomous: false);

        Assert.True(Resolve(update, out MovementStruct request, out _));
        Assert.Equal(MovementType.TurnToObject, request.Type);
        Assert.Equal(TargetGuid, request.ObjectId);
    }

    [Fact]
    public void AnOrderToFaceSomethingWithNoBodyTurnsToTheHeadingItNames()
    {
        var update = new WorldSession.EntityMotionUpdate(
            Guid: 0x50000001u,
            MotionState: new CreateObject.ServerMotionState(
                Stance: (ushort)0x3Du,
                ForwardCommand: null,
                MovementType: 8,
                TurnToPath: new CreateObject.TurnToPathData(
                    TargetGuid: TargetGuid,
                    WireHeading: 90f,
                    Bitfield: 0x3u,
                    Speed: 1f,
                    DesiredHeading: 0f)),
            InstanceSequence: 1,
            MovementSequence: 2,
            ServerControlSequence: 3,
            IsAutonomous: false);

        Assert.True(Resolve(
            update,
            out MovementStruct request,
            out _,
            targetRadius: null));
        Assert.Equal(MovementType.TurnToHeading, request.Type);
        Assert.Equal(90f, request.Params!.DesiredHeading);
    }
}
