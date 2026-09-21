using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The plugin tick runs at a fixed rate on every client, whatever rate the
/// client itself runs at. These hold the accumulator that makes that true:
/// the elapsed value a plugin is handed, how many ticks a stretch of real
/// time is worth, what happens to the leftovers, and what a stall costs.
///
/// Mutation check (2026-09-20), each run against a deliberately broken
/// clock: raising the tick with the fed delta instead of the step turned
/// <see cref="EveryTickCarriesExactlyOneStep"/> red; dropping the remainder
/// instead of carrying it turned <see cref="ShortFeedsAverageTheFixedRate"/>
/// and <see cref="TheRemainderIsCarriedIntoTheNextFeed"/> red; widening the
/// bound turned <see cref="AStallCostsAtMostTheCatchUpBound"/> red; keeping
/// the held time across a new generation turned
/// <see cref="ANewSessionRunStartsWithNothingHeld"/> red.
/// </summary>
public sealed class RuntimePluginTickClockTests
{
    [Fact]
    public void ATickIsRaisedOnlyWhenAWholeStepHasGoneBy()
    {
        var elapsed = new List<double>();
        var clock = new RuntimePluginTickClock(elapsed.Add);

        Assert.Equal(0, clock.Feed(0.007));
        Assert.Empty(elapsed);

        Assert.Equal(1, clock.Feed(0.008));
        Assert.Equal([RuntimePluginTickClock.StepSeconds], elapsed);
    }

    [Fact]
    public void EveryTickCarriesExactlyOneStep()
    {
        var elapsed = new List<double>();
        var clock = new RuntimePluginTickClock(elapsed.Add);

        Assert.Equal(4, clock.Feed(0.0605));

        Assert.All(
            elapsed,
            seconds => Assert.Equal(RuntimePluginTickClock.StepSeconds, seconds));
    }

    [Fact]
    public void ShortFeedsAverageTheFixedRate()
    {
        var elapsed = new List<double>();
        var clock = new RuntimePluginTickClock(elapsed.Add);

        // Ten seconds of a client running at about 143 frames a second.
        for (int frame = 0; frame < 10_000 / 7; frame++)
            clock.Feed(0.007);

        double seconds = (10_000 / 7) * 0.007;
        double expected = seconds / RuntimePluginTickClock.StepSeconds;
        Assert.InRange(elapsed.Count, (int)expected - 1, (int)expected + 1);
        Assert.Equal(
            elapsed.Count * RuntimePluginTickClock.StepSeconds,
            elapsed.Sum(),
            9);
    }

    [Fact]
    public void TheRemainderIsCarriedIntoTheNextFeed()
    {
        var elapsed = new List<double>();
        var clock = new RuntimePluginTickClock(elapsed.Add);

        Assert.Equal(1, clock.Feed(0.020));
        Assert.Equal(0.005, clock.HeldSeconds, 9);

        Assert.Equal(1, clock.Feed(0.010));
        Assert.Equal(0.0, clock.HeldSeconds, 9);
    }

    [Fact]
    public void AFeedOfOneStepIsWorthExactlyOneTick()
    {
        var elapsed = new List<double>();
        var clock = new RuntimePluginTickClock(elapsed.Add);

        for (int turn = 0; turn < 1_000; turn++)
            Assert.Equal(1, clock.Feed(RuntimePluginTickClock.StepSeconds));

        Assert.Equal(1_000, elapsed.Count);
        Assert.Equal(0.0, clock.HeldSeconds);
    }

    [Fact]
    public void AStallCostsAtMostTheCatchUpBound()
    {
        var elapsed = new List<double>();
        var clock = new RuntimePluginTickClock(elapsed.Add);

        Assert.Equal(RuntimePluginTickClock.MaxStepsPerFeed, clock.Feed(30.0));
        Assert.Equal(RuntimePluginTickClock.MaxStepsPerFeed, elapsed.Count);

        // The time past the bound is dropped rather than left as a debt the
        // next frame inherits.
        Assert.Equal(0.0, clock.HeldSeconds);
        Assert.Equal(0, clock.Feed(0.0));
    }

    [Fact]
    public void TheBoundIsTheStepsInTheLongestExistingFixedStretch()
    {
        // 0.2 s is the longest single stretch of elapsed time the client's
        // existing fixed-step clock hands to anything in one go.
        Assert.Equal(
            (int)(0.2 / RuntimePluginTickClock.StepSeconds),
            RuntimePluginTickClock.MaxStepsPerFeed);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ATimeThatIsNotAPositiveNumberOfSecondsCountsAsNoTime(double fed)
    {
        var elapsed = new List<double>();
        var clock = new RuntimePluginTickClock(elapsed.Add);

        clock.Feed(0.010);
        Assert.Equal(0, clock.Feed(fed));

        Assert.Empty(elapsed);
        Assert.Equal(0.010, clock.HeldSeconds, 9);
    }

    [Fact]
    public void ANewSessionRunStartsWithNothingHeld()
    {
        var elapsed = new List<double>();
        ulong generation = 1UL;
        var clock = new RuntimePluginTickClock(elapsed.Add, () => generation);

        Assert.Equal(0, clock.Feed(0.010));
        Assert.Equal(0.010, clock.HeldSeconds, 9);

        generation = 2UL;
        Assert.Equal(0, clock.Feed(0.010));

        // The 10 ms held by the run that ended is not spent on the new one.
        Assert.Empty(elapsed);
        Assert.Equal(0.010, clock.HeldSeconds, 9);
    }

    [Fact]
    public void ResetDropsTheTimeHeldWithoutRaisingAnything()
    {
        var elapsed = new List<double>();
        var clock = new RuntimePluginTickClock(elapsed.Add);

        clock.Feed(0.014);
        clock.Reset();

        Assert.Equal(0.0, clock.HeldSeconds);
        Assert.Equal(0, clock.Feed(0.0));
        Assert.Empty(elapsed);
    }

    [Fact]
    public void ARaiseIsRequired()
    {
        Assert.Throws<ArgumentNullException>(
            static () => new RuntimePluginTickClock(null!));
    }

    [Fact]
    public void FeedingTheClockAllocatesNothing()
    {
        int ticks = 0;
        ulong generation = 3UL;
        var clock = new RuntimePluginTickClock(
            _ => ticks++,
            () => generation);

        for (int frame = 0; frame < 10_000; frame++)
            clock.Feed(0.007);

        long minimumAllocated = long.MaxValue;
        for (int sample = 0; sample < 5; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 10_000; frame++)
                clock.Feed(0.007);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            minimumAllocated = Math.Min(minimumAllocated, allocated);
        }

        Assert.Equal(0L, minimumAllocated);
        Assert.True(ticks > 0);
    }
}
