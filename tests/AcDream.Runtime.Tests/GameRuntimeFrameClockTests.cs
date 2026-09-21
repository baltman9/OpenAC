using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Runtime.Tests.Support;
using AcDream.Runtime.World;

namespace AcDream.Runtime.Tests;

/// <summary>
/// One step of the frame clock, as every client takes it. The frame number
/// counts host frames and always moves. Simulation time is the world's own
/// clock and only moves while there is a world to simulate: between the
/// moment the previous world is given up and the moment the next one stands
/// up -- walking through a portal, arriving at login -- there is nothing for
/// elapsed time to mean, and time that ran through the gap would be handed
/// in one lump to whatever moves on the first frame after it.
///
/// This is the one place that rule is written down. Both clients ask for
/// their frame here rather than reaching for the clock themselves, so
/// neither can quietly keep its own version of it.
/// </summary>
public sealed class GameRuntimeFrameClockTests
{
    private const uint DestinationCell = 0x11340021u;

    [Fact]
    public void WithAWorldToSimulateTimeAndTheFrameNumberBothMove()
    {
        using var host = new NoWindowGameRuntimeHost();
        GameRuntime runtime = host.Runtime;

        RuntimeFrameTime first = runtime.AdvanceFrameClock(0.25);
        RuntimeFrameTime second = runtime.AdvanceFrameClock(0.25);

        Assert.Equal(1UL, first.FrameNumber);
        Assert.Equal(2UL, second.FrameNumber);
        Assert.Equal(0.25, second.DeltaSeconds, 6);
        Assert.Equal(0.5, runtime.Clock.SimulationTimeSeconds, 6);
    }

    [Fact]
    public void WithNoWorldToSimulateTheFrameNumberMovesAndTimeStandsStill()
    {
        using var host = new NoWindowGameRuntimeHost();
        GameRuntime runtime = host.Runtime;
        _ = runtime.AdvanceFrameClock(0.25);
        BeginPortal(runtime.TransitOwner);

        RuntimeFrameTime during = runtime.AdvanceFrameClock(0.25);

        Assert.Equal(2UL, during.FrameNumber);
        // The host frame still took the time it took: what is held back is
        // the world's clock, not the measurement of the frame.
        Assert.Equal(0.25, during.DeltaSeconds, 6);
        Assert.Equal(0.25, runtime.Clock.SimulationTimeSeconds, 6);
    }

    [Fact]
    public void OnceThereIsAWorldAgainTimeCarriesOnFromWhereItStopped()
    {
        using var host = new NoWindowGameRuntimeHost();
        GameRuntime runtime = host.Runtime;
        _ = runtime.AdvanceFrameClock(0.25);
        long generation = BeginPortal(runtime.TransitOwner);
        _ = runtime.AdvanceFrameClock(0.25);
        Assert.True(runtime.TransitOwner.Cancel(generation));

        _ = runtime.AdvanceFrameClock(0.25);

        Assert.Equal(0.5, runtime.Clock.SimulationTimeSeconds, 6);
    }

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
                    DestinationCell,
                    new Vector3(1f, 2f, 3f),
                    Quaternion.Identity)),
            teleportTimestampAdvanced: true));
        Assert.True(transit.TryBeginPortalReveal(
            1,
            DestinationCell,
            out long generation));
        Assert.False(transit.IsWorldSimulationAvailable);
        return generation;
    }
}
