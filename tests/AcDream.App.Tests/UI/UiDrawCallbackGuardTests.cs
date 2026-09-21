using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

/// <summary>
/// A drawing callback runs on the frame's critical path and inside somebody
/// else's push/pop pair. These pin what happens when one misbehaves: over
/// budget for long enough, throwing, or leaving the clip, alpha or origin
/// stack at a different depth than it found it.
/// </summary>
public sealed class UiDrawCallbackGuardTests
{
    private sealed class NullGpuFrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame => null;
    }

    /// <summary>A clock the test advances by hand, so nothing is timing-flaky.</summary>
    private sealed class ManualClock
    {
        public double Milliseconds { get; private set; }

        /// <summary>How much the next call will be told it took.</summary>
        public double NextCallCosts { get; set; }

        private bool _atCallStart = true;

        public double Read()
        {
            if (_atCallStart)
            {
                _atCallStart = false;
                return Milliseconds;
            }
            _atCallStart = true;
            Milliseconds += NextCallCosts;
            return Milliseconds;
        }
    }

    private static UiRenderContext Context()
    {
        var device = new RecordingGpuDevice();
        var renderer = new TextRenderer(device, new NullGpuFrameSource(), "unused");
        renderer.Begin(new Vector2(800f, 600f));
        return new UiRenderContext(renderer, new Vector2(800f, 600f));
    }

    private static (UiDrawCallbackGuard Guard, ManualClock Clock, List<string> Reports) Build()
    {
        var clock = new ManualClock();
        var reports = new List<string>();
        var guard = new UiDrawCallbackGuard("TestCallback", reports.Add, clock.Read);
        return (guard, clock, reports);
    }

    private static double OverBudget => UiDrawCallbackGuard.FrameBudgetMilliseconds * 2.0;
    private static double WithinBudget => UiDrawCallbackGuard.FrameBudgetMilliseconds / 4.0;

    [Fact]
    public void AWellBehavedCallbackKeepsBeingCalled()
    {
        (UiDrawCallbackGuard guard, ManualClock clock, List<string> reports) = Build();
        UiRenderContext context = Context();
        clock.NextCallCosts = WithinBudget;
        int calls = 0;

        for (int frame = 0; frame < 50; frame++)
            Assert.True(guard.Invoke(context, _ => calls++));

        Assert.Equal(50, calls);
        Assert.False(guard.IsTripped);
        Assert.Empty(reports);
    }

    [Fact]
    public void ItTakesExactlyTheNamedNumberOfOverrunsInARowToDropIt()
    {
        (UiDrawCallbackGuard guard, ManualClock clock, _) = Build();
        UiRenderContext context = Context();
        clock.NextCallCosts = OverBudget;

        for (int overrun = 1; overrun < UiDrawCallbackGuard.ConsecutiveOverrunsBeforeTrip; overrun++)
        {
            guard.Invoke(context, static _ => { });
            Assert.False(guard.IsTripped);
            Assert.Equal(overrun, guard.ConsecutiveOverruns);
        }

        guard.Invoke(context, static _ => { });

        Assert.True(guard.IsTripped);
    }

    [Fact]
    public void OneFrameBackInsideTheBudgetClearsTheRun()
    {
        (UiDrawCallbackGuard guard, ManualClock clock, _) = Build();
        UiRenderContext context = Context();
        int calls = 0;

        // One short of the trip, then a good frame, then one short again.
        for (int round = 0; round < 2; round++)
        {
            clock.NextCallCosts = OverBudget;
            for (int overrun = 0;
                 overrun < UiDrawCallbackGuard.ConsecutiveOverrunsBeforeTrip - 1;
                 overrun++)
            {
                guard.Invoke(context, _ => calls++);
            }
            clock.NextCallCosts = WithinBudget;
            guard.Invoke(context, _ => calls++);
            Assert.Equal(0, guard.ConsecutiveOverruns);
        }

        Assert.False(guard.IsTripped);
        Assert.Equal(2 * UiDrawCallbackGuard.ConsecutiveOverrunsBeforeTrip, calls);
    }

    [Fact]
    public void ADroppedCallbackIsNeverCalledAgainAndIsReportedOnlyOnce()
    {
        (UiDrawCallbackGuard guard, ManualClock clock, List<string> reports) = Build();
        UiRenderContext context = Context();
        clock.NextCallCosts = OverBudget;
        int calls = 0;

        for (int frame = 0; frame < 200; frame++)
            guard.Invoke(context, _ => calls++);

        Assert.True(guard.IsTripped);
        Assert.Equal(UiDrawCallbackGuard.ConsecutiveOverrunsBeforeTrip, calls);
        Assert.Single(reports);
        Assert.Contains("TestCallback", reports[0]);
    }

    [Fact]
    public void ACallbackThatThrowsIsDroppedAtOnceAndTheThrowIsReported()
    {
        (UiDrawCallbackGuard guard, ManualClock clock, List<string> reports) = Build();
        UiRenderContext context = Context();
        clock.NextCallCosts = WithinBudget;
        int calls = 0;

        bool ran = guard.Invoke(context, _ =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        });

        Assert.False(ran);
        Assert.True(guard.IsTripped);
        guard.Invoke(context, _ => calls++);
        Assert.Equal(1, calls);
        Assert.Single(reports);
        Assert.Contains("boom", reports[0]);
    }

    [Fact]
    public void AThrowingCallbackLeavesTheThreeStacksWhereItFoundThem()
    {
        (UiDrawCallbackGuard guard, ManualClock clock, _) = Build();
        UiRenderContext context = Context();
        clock.NextCallCosts = WithinBudget;
        context.PushTransform(10f, 10f);
        context.PushClip(0f, 0f, 100f, 100f);
        context.PushAlpha(0.5f);
        (int clip, int transform, int alpha) before = Depths(context);

        guard.Invoke(context, c =>
        {
            c.PushTransform(5f, 5f);
            c.PushClip(0f, 0f, 10f, 10f);
            c.PushAlpha(0.5f);
            throw new InvalidOperationException("boom");
        });

        Assert.Equal(before, Depths(context));
        // And the caller's own state is still what it pushed.
        Assert.Equal(new Vector2(10f, 10f), context.CurrentOrigin);
        Assert.Equal(0.5f, context.AlphaMod, 5);
    }

    [Fact]
    public void ACallbackThatLeavesAPushBehindIsDroppedAndTheDepthIsPutBack()
    {
        (UiDrawCallbackGuard guard, ManualClock clock, List<string> reports) = Build();
        UiRenderContext context = Context();
        clock.NextCallCosts = WithinBudget;
        (int clip, int transform, int alpha) before = Depths(context);

        bool ran = guard.Invoke(context, static c =>
        {
            c.PushClip(0f, 0f, 10f, 10f);
            c.PushTransform(1f, 1f);
        });

        Assert.False(ran);
        Assert.True(guard.IsTripped);
        Assert.Equal(before, Depths(context));
        Assert.Single(reports);
    }

    [Fact]
    public void ACallbackThatPopsALevelItDidNotOwnIsDroppedAndTheDepthIsPutBack()
    {
        (UiDrawCallbackGuard guard, ManualClock clock, _) = Build();
        UiRenderContext context = Context();
        clock.NextCallCosts = WithinBudget;
        context.PushTransform(10f, 10f);
        context.PushClip(0f, 0f, 100f, 100f);
        context.PushAlpha(0.5f);
        (int clip, int transform, int alpha) before = Depths(context);

        bool ran = guard.Invoke(context, static c =>
        {
            c.PopClip();
            c.PopTransform();
            c.PopAlpha();
        });

        Assert.False(ran);
        Assert.True(guard.IsTripped);
        // The values it ate are gone, but the depth is paired again, so the
        // caller's remaining pops match its remaining pushes.
        Assert.Equal(before, Depths(context));
    }

    [Fact]
    public void AnOverrunThatDropsTheCallbackStillCountsAsHavingDrawn()
    {
        (UiDrawCallbackGuard guard, ManualClock clock, _) = Build();
        UiRenderContext context = Context();
        clock.NextCallCosts = OverBudget;

        bool ran = false;
        for (int frame = 0; frame < UiDrawCallbackGuard.ConsecutiveOverrunsBeforeTrip; frame++)
            ran = guard.Invoke(context, static _ => { });

        Assert.True(ran);
        Assert.True(guard.IsTripped);
        Assert.False(guard.Invoke(context, static _ => { }));
    }

    [Fact]
    public void TheMeasuredCostOfTheLastCallIsReadable()
    {
        (UiDrawCallbackGuard guard, ManualClock clock, _) = Build();
        UiRenderContext context = Context();

        clock.NextCallCosts = WithinBudget;
        guard.Invoke(context, static _ => { });
        Assert.Equal(WithinBudget, guard.LastMilliseconds, 6);

        clock.NextCallCosts = OverBudget;
        guard.Invoke(context, static _ => { });
        Assert.Equal(OverBudget, guard.LastMilliseconds, 6);
    }

    private static (int Clip, int Transform, int Alpha) Depths(UiRenderContext context)
        => (context.ClipStackDepth, context.TransformStackDepth, context.AlphaStackDepth);
}
