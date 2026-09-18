using System.Numerics;
using AcDream.App.UI;

namespace AcDream.App.Tests.UI;

/// <summary>
/// One render context serves every frame, so a context beginning a frame must
/// start where a freshly built one starts, and a frame on a warmed context
/// must take no new stack arrays.
/// </summary>
public sealed class UiRenderContextReuseTests
{
    [Fact]
    public void BeginningAFrameClearsWhatTheLastFrameLeft()
    {
        UiRenderContext reused = Context();
        reused.PushTransform(17f, 23f);
        reused.PushClip(1f, 2f, 3f, 4f);
        reused.PushAlpha(0.25f);

        reused.Begin(new Vector2(640f, 480f), null);
        UiRenderContext fresh = Context();
        fresh.Begin(new Vector2(640f, 480f), null);

        Assert.Equal(fresh.ScreenSize, reused.ScreenSize);
        Assert.Equal(fresh.AlphaMod, reused.AlphaMod);
        Assert.Equal(fresh.CurrentOrigin, reused.CurrentOrigin);
        Assert.Equal(fresh.CurrentClipIsEmpty, reused.CurrentClipIsEmpty);
        Assert.Equal(fresh.ClipStackDepth, reused.ClipStackDepth);

        // A pop with nothing pushed leaves the state alone, exactly as it
        // does on a context that has never drawn.
        reused.PopTransform();
        reused.PopClip();
        reused.PopAlpha();
        Assert.Equal(fresh.CurrentOrigin, reused.CurrentOrigin);
        Assert.Equal(fresh.AlphaMod, reused.AlphaMod);
        Assert.Equal(fresh.ClipStackDepth, reused.ClipStackDepth);
    }

    [Fact]
    public void AFrameOnAWarmedContextTakesNoNewStackArrays()
    {
        UiRenderContext context = Context();

        ZeroAllocationProbe.AssertAllocatesNothing(
            "UiRenderContext frame",
            () =>
            {
                context.Begin(new Vector2(640f, 480f), null);
                for (int depth = 0; depth < 8; depth++)
                {
                    context.PushTransform(depth, depth);
                    context.PushClip(0f, 0f, 640f, 480f);
                    context.PushAlpha(0.9f);
                }

                for (int depth = 0; depth < 8; depth++)
                {
                    context.PopAlpha();
                    context.PopClip();
                    context.PopTransform();
                }
            });
    }

    private static UiRenderContext Context() =>
        new(null!, new Vector2(640f, 480f));
}
