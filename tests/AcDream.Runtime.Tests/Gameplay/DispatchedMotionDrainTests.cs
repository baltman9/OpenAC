using AcDream.Core.Physics;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// The drain a host that plays no animations uses to finish the motions it
/// dispatched. It has to empty the queue: anything left outstanding suspends
/// the whole move-to layer.
/// </summary>
public sealed class DispatchedMotionDrainTests
{
    [Fact]
    public void TwoIdenticalQueuedMotionsBothLeaveTheQueue()
    {
        var motion = new MotionInterpreter(new PhysicsBody());
        // Production really does queue these in pairs: stopping an
        // interpreted motion always adds the same "ready" entry, and an
        // arrival issues two stops sharing one parameter block.
        motion.AddToQueue(0u, (uint)MotionCommand.Ready, 0u);
        motion.AddToQueue(0u, (uint)MotionCommand.Ready, 0u);
        Assert.Equal(2, motion.PendingMotionCount);

        RuntimeLocalPlayerPhysicsPublicationState.CompleteDispatchedMotions(motion);

        Assert.Equal(0, motion.PendingMotionCount);
        Assert.False(motion.MotionsPending());
    }

    [Fact]
    public void OneFrameOfDrainingIsBoundedByItsCeiling()
    {
        var motion = new MotionInterpreter(new PhysicsBody());
        for (int queued = 0; queued < 70; queued++)
            motion.AddToQueue(0u, (uint)MotionCommand.Ready, 0u);

        RuntimeLocalPlayerPhysicsPublicationState.CompleteDispatchedMotions(motion);

        Assert.Equal(6, motion.PendingMotionCount);
    }

    [Fact]
    public void ADrainWithNoBodyTakesNothingOffTheQueue()
    {
        // Without a body the interpreter completes nothing, and the drain must
        // go through it rather than empty the queue behind its back.
        var motion = new MotionInterpreter();
        for (int queued = 0; queued < 70; queued++)
            motion.AddToQueue(0u, (uint)MotionCommand.Ready, 0u);

        RuntimeLocalPlayerPhysicsPublicationState.CompleteDispatchedMotions(motion);

        Assert.Equal(70, motion.PendingMotionCount);
    }
}
