using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using Xunit;

namespace AcDream.Core.Tests.Physics;

/// <summary>
/// Establishing the run-hold key that was already true before anything was
/// watching, versus reacting to somebody pressing it.
/// </summary>
public class MotionInterpreterHoldKeySeedTests
{
    private static MotionInterpreter GroundedInterp()
    {
        var body = new PhysicsBody();
        body.State |= PhysicsStateFlags.Gravity;
        body.TransientState |= TransientStateFlags.Contact
                             | TransientStateFlags.OnWalkable
                             | TransientStateFlags.Active;
        // The local player's own body: its movement is its own, which is what
        // sends a hold-key edge down the re-derive-from-raw-keys path.
        body.LastMoveWasAutonomous = true;
        return new MotionInterpreter(body);
    }

    [Fact]
    public void SeedingTheHoldKeyLeavesAMotionTheKeysNeverHeld()
    {
        MotionInterpreter motion = GroundedInterp();
        // A turn the move-to layer owns: it is in the interpreted state and
        // was never in the raw key state.
        motion.DoInterpretedMotion(MotionCommand.TurnRight, new MovementParameters());
        Assert.Equal(MotionCommand.TurnRight, motion.InterpretedState.TurnCommand);
        Assert.Equal(0u, motion.RawState.TurnCommand);

        motion.SeedHoldRun(true);

        Assert.Equal(HoldKey.Run, motion.RawState.CurrentHoldKey);
        Assert.Equal(MotionCommand.TurnRight, motion.InterpretedState.TurnCommand);
    }

    /// <summary>
    /// The companion: an actual key edge DOES re-derive the interpreted state
    /// from the keys, which is why the seed must not go through it.
    /// </summary>
    [Fact]
    public void AnActualRunKeyEdgeReDerivesTheStateFromTheKeys()
    {
        MotionInterpreter motion = GroundedInterp();
        motion.DoInterpretedMotion(MotionCommand.TurnRight, new MovementParameters());
        Assert.Equal(MotionCommand.TurnRight, motion.InterpretedState.TurnCommand);

        motion.set_hold_run(true, interrupt: false);

        Assert.Equal(HoldKey.Run, motion.RawState.CurrentHoldKey);
        Assert.Equal(0u, motion.InterpretedState.TurnCommand);
    }
}
