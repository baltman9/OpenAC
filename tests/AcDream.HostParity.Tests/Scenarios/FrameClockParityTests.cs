using AcDream.Core.Physics;
using AcDream.Runtime;
using AcDream.Runtime.World;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The world's clock, across the moment the world is handed over. Walking
/// through a portal gives up the world the character was standing in before
/// the next one is up; between those two moments elapsed time means nothing,
/// so the world's clock stands still and starts again where it stopped.
///
/// A plugin reads that clock through everything timed against it -- pacing
/// between two uses, how long a cast has been running, how stale a body's
/// last known position is. A client that ran time through the gap would hand
/// all of it to the first frame on the other side, and a bot written against
/// one client would misjudge every one of those on the other.
///
/// Mutation check (2026-09-20), run: making the runtime's frame step advance
/// the world's clock unconditionally turned this red and left the other
/// scenarios green.
/// </summary>
public sealed class FrameClockParityTests
{
    private const uint Destination = 0x11340021u;

    [Fact]
    public void TheWorldClockStandsStillWhileThereIsNoWorldOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);

            transcript.Step("a frame with a world to simulate");
            double before = arm.Runtime.Clock.SimulationTimeSeconds;
            arm.Advance();
            double afterOneFrame = arm.Runtime.Clock.SimulationTimeSeconds;
            transcript.Record("ran", afterOneFrame > before);
            Assert.True(afterOneFrame > before);

            transcript.Step("the character gives up the world it is in");
            long generation = BeginPortal(arm.Runtime.TransitOwner);
            transcript.Record(
                "simulating",
                arm.Runtime.TransitOwner.IsWorldSimulationAvailable);
            arm.Advance();
            arm.Advance();
            double afterTheGap = arm.Runtime.Clock.SimulationTimeSeconds;
            transcript.Record("held", afterTheGap == afterOneFrame);
            // Said outright: two clients that both ran the clock through the
            // handover would write identical transcripts.
            Assert.Equal(afterOneFrame, afterTheGap, 9);
            // The frame number is not held: the client is still running.
            transcript.Record("frames", arm.Runtime.Clock.FrameNumber);

            transcript.Step("and the next world stands up");
            Assert.True(arm.Runtime.TransitOwner.Cancel(generation));
            arm.Advance();
            double afterwards = arm.Runtime.Clock.SimulationTimeSeconds;
            transcript.Record("resumed", afterwards > afterTheGap);
            Assert.True(afterwards > afterTheGap);
            // It carries on from where it stopped rather than catching up on
            // the frames it sat out.
            transcript.Record(
                "step", Math.Round(afterwards - afterTheGap, 6));
            Assert.Equal(ParityArm.TickSeconds, afterwards - afterTheGap, 9);
        });

    private static long BeginPortal(RuntimeWorldTransitState transit)
    {
        Assert.True(transit.TryQueueTeleportStart(1));
        Assert.True(transit.ActivateQueuedTeleport());
        Assert.True(transit.OfferTeleportDestination(
            new RuntimeTeleportDestination(
                EntityGuid: 0x50000001u,
                InstanceSequence: 1,
                PositionSequence: 1,
                TeleportSequence: 1,
                ForcePositionSequence: 1,
                Position: new Position(
                    Destination,
                    new System.Numerics.Vector3(1f, 2f, 3f),
                    System.Numerics.Quaternion.Identity)),
            teleportTimestampAdvanced: true));
        Assert.True(transit.TryBeginPortalReveal(
            1,
            Destination,
            out long generation));
        return generation;
    }
}
