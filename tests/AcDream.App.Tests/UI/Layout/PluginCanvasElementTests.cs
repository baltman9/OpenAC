using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// A plugin canvas on the shared overlay layer: painted into its own
/// off-screen target only when invalidated, at most once a frame, under
/// the guard, with a painter that dies with the call; shown as one quad
/// on the interface; taken down through the tree and the retirement queue.
/// </summary>
public sealed class PluginCanvasElementTests
{
    private sealed class FrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame { get; set; }
    }

    private sealed class HeldRetirementQueue : IGpuResourceRetirementQueue
    {
        public List<Action> Pending { get; } = [];
        public void Retire(Action release) => Pending.Add(release);
        public void RunAll()
        {
            foreach (Action release in Pending) release();
            Pending.Clear();
        }
    }

    private sealed class FakeImageBackend : IPluginImageBackend
    {
        public const uint ArtTexture = 77u;

        public bool TryGetClientArt(uint surfaceId, out uint texture, out int width, out int height)
        {
            texture = ArtTexture; width = 16; height = 16;
            return true;
        }

        public bool TryGetSpellIcon(uint spellId, out uint texture, out int width, out int height)
        {
            texture = 0u; width = 0; height = 0;
            return false;
        }

        public bool TryGetObjectIcon(uint objectId, out uint texture, out int width, out int height)
        {
            texture = 0u; width = 0; height = 0;
            return false;
        }

        public uint UploadOwned(byte[] rgba, int width, int height, string debugName) => 0u;
        public bool ReleaseOwned(uint texture) => false;
    }

    /// <summary>A clock the test advances by hand: each guarded call costs what the test says.</summary>
    private sealed class ManualClock
    {
        public double Milliseconds { get; private set; }
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

    /// <summary>
    /// The interface as the canvas sees it: a root, the overlay host with a
    /// canvas layer, the registry, the shared surface, and a main renderer
    /// the frame is drawn into. Mounting mirrors the runtime's mount path.
    /// </summary>
    private sealed class Harness
    {
        public RecordingGpuDevice Device { get; } = new();
        public FrameSource Frames { get; } = new();
        public HeldRetirementQueue Retirement { get; } = new();
        public BufferedUiRegistry Registry { get; } = new();
        public UiRoot Root { get; } = new() { Width = 800f, Height = 600f };
        public UiOverlayHost Host { get; }
        public UiOverlayLayer Layer { get; }
        public PluginCanvasSurface Surface { get; }
        public TextRenderer MainRenderer { get; }
        public UiRenderContext MainContext { get; }
        public List<string> Reports { get; } = [];
        public ManualClock Clock { get; } = new();
        public PluginUiOwner Owner { get; } = new("example.plugin", "Example");
        private long _now;

        public Harness()
        {
            Host = UiOverlayHost.Mount(Root);
            Layer = Host.AddLayer("PluginCanvases");
            Layer.Visible = true;
            var services = new PluginCanvasHostServices(Device, Frames, "unused", null, Retirement);
            Surface = new PluginCanvasSurface(services, font: null);
            MainRenderer = new TextRenderer(Device, Frames, "unused");
            MainContext = new UiRenderContext(MainRenderer, new Vector2(800f, 600f));
            Registry.BindImageServices(new FakeImageBackend());
            Device.Clear();
        }

        public (PluginCanvasRegistration Registration, PluginCanvasElement Element) Mount(
            PluginCanvasDescriptor descriptor, Action<IPluginPainter> paint)
        {
            var registration = (PluginCanvasRegistration)Registry.RegisterCanvas(Owner, descriptor, paint);
            PluginCanvasRegistration drained = Assert.Single(Registry.DrainCanvases());
            Assert.Same(registration, drained);
            var element = new PluginCanvasElement(
                registration, Surface, () => Registry.FindImages(Owner), Reports.Add, Clock.Read);
            Layer.AddChild(element);
            Registry.CompleteCanvasMount(registration, () =>
            {
                Layer.RemoveChild(element);
                element.ReleaseTargets();
            });
            return (registration, element);
        }

        /// <summary>One interface frame: tick, then draw everything into an open GPU frame.</summary>
        public void Frame()
        {
            using IGpuFrame frame = Device.BeginFrame();
            Frames.CurrentFrame = frame;
            try
            {
                _now += 16;
                Root.Tick(0.016, _now);
                MainRenderer.Begin(new Vector2(Root.Width, Root.Height));
                MainContext.Begin(new Vector2(Root.Width, Root.Height), null);
                Root.Draw(MainContext);
                MainRenderer.Flush(null);
            }
            finally
            {
                Frames.CurrentFrame = null;
            }
        }

        public IReadOnlyList<(uint Texture, int VertexCount, float Alpha)> SurfaceRuns =>
            Surface.Renderer.DebugSpriteSegments;

        public IReadOnlyList<(uint Texture, int VertexCount, float Alpha)> MainRuns =>
            MainRenderer.DebugSpriteSegments;
    }

    private static PluginCanvasDescriptor Hud(bool visible = true) =>
        new("hud", 200, 100)
        {
            Anchor = PluginCanvasAnchor.BottomRight,
            Offset = new PluginPoint(-10, -10),
            StartVisible = visible,
        };

    [Fact]
    public void APaintLandsAsSpriteRunsOnTheSurfaceAndOneBlitOnTheInterface()
    {
        var harness = new Harness();
        PluginImage art = harness.Registry.ImagesFor(harness.Owner).FromClientArt(1u);
        int paints = 0;
        (PluginCanvasRegistration registration, PluginCanvasElement element) = harness.Mount(Hud(), painter =>
        {
            paints++;
            painter.FillRect(new PluginRect(0, 0, 200, 100), new PluginColor(0, 0, 0, 160));
            painter.DrawImage(art, new PluginRect(10, 10, 32, 32), PluginColor.White);
            painter.DrawLine(new PluginPoint(10, 90), new PluginPoint(190, 10), PluginColor.White, 2f);
        });

        harness.Frame();

        Assert.Equal(1, paints);
        Assert.True(registration.IsAvailable);
        // The fill and the line are untextured and batch together; the image is its own run.
        Assert.Equal(
            [0u, FakeImageBackend.ArtTexture, 0u],
            harness.SurfaceRuns.Select(run => run.Texture).ToArray());
        Assert.Equal([6, 6, 6], harness.SurfaceRuns.Select(run => run.VertexCount).ToArray());

        // One target of the canvas's size, painted in its own pass before the interface's.
        GpuRecordedRenderTargetCreate created = Assert.Single(harness.Device.OfKind<GpuRecordedRenderTargetCreate>());
        Assert.Equal((200, 100), (created.Description.Width, created.Description.Height));
        string[] passes = harness.Device.OfKind<GpuRecordedPassBegin>().Select(pass => pass.Name).ToArray();
        Assert.Equal([PluginCanvasSurface.PassName, "ui-text"], passes);

        // The interface draws the canvas as one quad of the painted texture.
        uint shown = element.ShownTextureHandle;
        Assert.NotEqual(0u, shown);
        (uint Texture, int VertexCount, float Alpha) blit = Assert.Single(harness.MainRuns);
        Assert.Equal(shown, blit.Texture);
        Assert.Equal(6, blit.VertexCount);
    }

    [Fact]
    public void ACanvasIsRepaintedOnlyWhenInvalidatedAndAtMostOnceAFrame()
    {
        var harness = new Harness();
        int paints = 0;
        (PluginCanvasRegistration registration, PluginCanvasElement element) =
            harness.Mount(Hud(), painter => { paints++; painter.Clear(PluginColor.Transparent); });

        harness.Frame();
        uint first = element.ShownTextureHandle;
        harness.Frame();
        harness.Frame();

        Assert.Equal(1, paints);
        Assert.Equal(first, element.ShownTextureHandle);
        Assert.Equal(1, element.TargetCount);
        Assert.Single(harness.MainRuns);

        registration.Invalidate();
        registration.Invalidate();
        harness.Frame();

        Assert.Equal(2, paints);
        // Painted into a target other than the one a frame might still be reading.
        Assert.NotEqual(first, element.ShownTextureHandle);
        Assert.Equal(2, element.TargetCount);
    }

    [Fact]
    public void NothingIsPaintedWhenNoFrameIsOpenAndTheRequestIsKept()
    {
        var harness = new Harness();
        int paints = 0;
        (PluginCanvasRegistration registration, PluginCanvasElement element) =
            harness.Mount(Hud(), _ => paints++);

        harness.Root.Tick(0.016, 16);
        harness.MainRenderer.Begin(new Vector2(800f, 600f));
        harness.MainContext.Begin(new Vector2(800f, 600f), null);
        harness.Root.Draw(harness.MainContext);

        Assert.Equal(0, paints);
        Assert.Equal(0u, element.ShownTextureHandle);
        Assert.Empty(harness.MainRuns);

        harness.Frame();
        Assert.Equal(1, paints);
        _ = registration;
    }

    [Fact]
    public void ASlowPainterIsDroppedAfterThreeOverrunsAndTheCanvasHidden()
    {
        var harness = new Harness();
        harness.Clock.NextCallCosts = PluginCanvasElement.RepaintBudgetMilliseconds + 1.0;
        int paints = 0;
        (PluginCanvasRegistration registration, PluginCanvasElement element) =
            harness.Mount(Hud(), _ => paints++);

        for (int frame = 0; frame < UiDrawCallbackGuard.ConsecutiveOverrunsBeforeTrip; frame++)
        {
            registration.Invalidate();
            harness.Frame();
        }

        Assert.Equal(UiDrawCallbackGuard.ConsecutiveOverrunsBeforeTrip, paints);
        Assert.True(element.Guard.IsTripped);
        Assert.True(registration.IsDropped);
        Assert.False(registration.IsAvailable);
        Assert.False(element.Visible);
        Assert.Equal(0u, element.ShownTextureHandle);
        string report = Assert.Single(harness.Reports);
        Assert.Contains("example.plugin/hud", report, StringComparison.Ordinal);
        Assert.Contains("dropped", report, StringComparison.Ordinal);

        registration.Invalidate();
        harness.Frame();
        Assert.Equal(UiDrawCallbackGuard.ConsecutiveOverrunsBeforeTrip, paints);
        Assert.Empty(harness.MainRuns);
    }

    [Fact]
    public void ASlowFrameNowAndThenIsForgiven()
    {
        var harness = new Harness();
        int paints = 0;
        (PluginCanvasRegistration registration, PluginCanvasElement element) =
            harness.Mount(Hud(), _ => paints++);

        harness.Clock.NextCallCosts = PluginCanvasElement.RepaintBudgetMilliseconds + 1.0;
        registration.Invalidate();
        harness.Frame();
        registration.Invalidate();
        harness.Frame();
        harness.Clock.NextCallCosts = 0.5;
        registration.Invalidate();
        harness.Frame();
        harness.Clock.NextCallCosts = PluginCanvasElement.RepaintBudgetMilliseconds + 1.0;
        registration.Invalidate();
        harness.Frame();

        Assert.Equal(4, paints);
        Assert.False(element.Guard.IsTripped);
        Assert.True(registration.IsAvailable);
    }

    [Fact]
    public void APainterKeptPastItsCallbackThrows()
    {
        var harness = new Harness();
        IPluginPainter? kept = null;
        (PluginCanvasRegistration registration, _) = harness.Mount(Hud(), painter => kept = painter);

        harness.Frame();

        Assert.NotNull(kept);
        Assert.Throws<InvalidOperationException>(() => kept.FillRect(new PluginRect(0, 0, 1, 1), PluginColor.White));
        Assert.Throws<InvalidOperationException>(() => kept.MeasureText("x"));
        Assert.Throws<InvalidOperationException>(() => kept.PushClip(new PluginRect(0, 0, 1, 1)));
        Assert.True(registration.IsAvailable);
    }

    [Fact]
    public void APainterThatLeavesAClipPushedIsDroppedAtOnceAndTheInterfaceIsUnharmed()
    {
        var harness = new Harness();
        int paints = 0;
        (PluginCanvasRegistration registration, PluginCanvasElement element) = harness.Mount(Hud(), painter =>
        {
            paints++;
            painter.PushClip(new PluginRect(0, 0, 10, 10));
        });

        harness.Frame();

        Assert.Equal(1, paints);
        Assert.True(element.Guard.IsTripped);
        Assert.True(registration.IsDropped);
        Assert.False(element.Visible);
        Assert.Equal(0, harness.MainContext.ClipStackDepth);
        Assert.Contains("unbalanced", Assert.Single(harness.Reports), StringComparison.Ordinal);
    }

    [Fact]
    public void APainterThatThrowsIsDroppedAtOnce()
    {
        var harness = new Harness();
        (PluginCanvasRegistration registration, PluginCanvasElement element) =
            harness.Mount(Hud(), _ => throw new InvalidOperationException("boom"));

        harness.Frame();

        Assert.True(element.Guard.IsTripped);
        Assert.False(registration.IsAvailable);
        Assert.Contains("boom", Assert.Single(harness.Reports), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryPrimitiveDrawsInsideTheCanvasAndClipsToIt()
    {
        var harness = new Harness();
        PluginImage art = harness.Registry.ImagesFor(harness.Owner).FromClientArt(1u);
        (PluginCanvasRegistration registration, _) = harness.Mount(Hud(), painter =>
        {
            Assert.Equal((200, 100), (painter.Width, painter.Height));
            painter.Clear(new PluginColor(1, 2, 3, 4));
            painter.StrokeRect(new PluginRect(0, 0, 200, 100), PluginColor.White, 2f);
            painter.PushClip(new PluginRect(0, 0, 50, 50));
            painter.DrawImage(art, new PluginRect(40, 40, 32, 32), PluginColor.White);
            painter.PopClip();
            // Wholly outside the canvas: clipped away, no run.
            painter.DrawImageTransformed(
                art, new PluginRect(500, 500, 32, 32), PluginColor.White, 0.5, new PluginPoint(16, 16));
            // Text without a font draws nothing and measures nothing, without throwing.
            painter.DrawText("hello", new PluginPoint(1, 1), PluginColor.White, outline: true);
            Assert.Equal(default, painter.MeasureText("hello"));
        });

        harness.Frame();

        Assert.True(registration.IsAvailable);
        // clear + four outline strips = five untextured quads in one run; then the clipped image.
        Assert.Equal([0u, FakeImageBackend.ArtTexture], harness.SurfaceRuns.Select(run => run.Texture).ToArray());
        Assert.Equal(30, harness.SurfaceRuns[0].VertexCount);
        IReadOnlyList<float> imageVerts = harness.Surface.Renderer.DebugSpriteSegmentVerts[1].Verts;
        float maxX = 0f, maxY = 0f;
        for (int i = 0; i < imageVerts.Count; i += TextRenderer.FloatsPerVertex)
        {
            maxX = MathF.Max(maxX, imageVerts[i]);
            maxY = MathF.Max(maxY, imageVerts[i + 1]);
        }
        Assert.Equal((50f, 50f), (maxX, maxY));
    }

    [Fact]
    public void TeardownRemovesTheElementRetiresTheTexturesAndDropsThePaintDelegate()
    {
        var harness = new Harness();
        (PluginCanvasRegistration registration, PluginCanvasElement element, WeakReference paintTarget) =
            MountWithCollectablePainter(harness);
        harness.Frame();
        registration.Invalidate();
        harness.Frame();
        Assert.Equal(2, element.TargetCount);
        int slotsBefore = harness.Device.LiveTextureSlotCount;

        registration.Dispose();

        Assert.DoesNotContain(element, harness.Layer.Children);
        Assert.Null(registration.Paint);
        Assert.False(registration.IsAvailable);
        Assert.Equal(0, harness.Registry.CanvasCount);
        // The textures go through the queue, not now: a frame may still be reading the shown one.
        Assert.Equal(2, harness.Retirement.Pending.Count);
        Assert.Equal(slotsBefore, harness.Device.LiveTextureSlotCount);
        harness.Retirement.RunAll();
        Assert.Equal(slotsBefore - 2, harness.Device.LiveTextureSlotCount);

        // Nothing the interface holds points into the plugin's code any more.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.False(paintTarget.IsAlive);

        // A frame after teardown draws nothing for it.
        harness.Frame();
        Assert.Empty(harness.MainRuns);
    }

    private static (PluginCanvasRegistration, PluginCanvasElement, WeakReference) MountWithCollectablePainter(
        Harness harness)
    {
        var target = new CollectablePainter();
        (PluginCanvasRegistration registration, PluginCanvasElement element) =
            harness.Mount(Hud(), target.Paint);
        return (registration, element, new WeakReference(target));
    }

    private sealed class CollectablePainter
    {
        public void Paint(IPluginPainter painter) => painter.Clear(PluginColor.Transparent);
    }

    [Fact]
    public void ACanvasIsPlacedByAnchorPlusOffsetAndFollowsTheViewport()
    {
        var harness = new Harness();
        (PluginCanvasRegistration registration, PluginCanvasElement element) =
            harness.Mount(Hud(), painter => painter.Clear(PluginColor.Transparent));

        harness.Frame();
        Assert.Equal((590f, 490f), (element.Left, element.Top));
        Assert.Equal((200f, 100f), (element.Width, element.Height));
        Assert.True(element.ClickThrough);

        registration.Anchor = PluginCanvasAnchor.Center;
        registration.Offset = new PluginPoint(0, 0);
        harness.Frame();
        Assert.Equal((300f, 250f), (element.Left, element.Top));

        registration.Anchor = PluginCanvasAnchor.TopLeft;
        registration.Offset = new PluginPoint(7, 9);
        harness.Frame();
        Assert.Equal((7f, 9f), (element.Left, element.Top));

        registration.Anchor = PluginCanvasAnchor.BottomRight;
        registration.Offset = new PluginPoint(-10, -10);
        harness.Host.SetViewport(new Vector2(1024f, 768f));
        harness.Frame();
        Assert.Equal((814f, 658f), (element.Left, element.Top));
    }

    [Fact]
    public void VisibilityFollowsTheRegistrationAndWhatWasPaintedIsKeptWhileHidden()
    {
        var harness = new Harness();
        int paints = 0;
        (PluginCanvasRegistration registration, PluginCanvasElement element) =
            harness.Mount(Hud(visible: false), painter => { paints++; painter.Clear(PluginColor.Transparent); });

        harness.Frame();
        Assert.False(element.Visible);
        Assert.Equal(0, paints);
        Assert.Empty(harness.MainRuns);

        registration.IsVisible = true;
        harness.Frame();
        Assert.True(element.Visible);
        Assert.Equal(1, paints);
        uint shown = element.ShownTextureHandle;
        Assert.Single(harness.MainRuns);

        registration.IsVisible = false;
        harness.Frame();
        Assert.Empty(harness.MainRuns);
        registration.IsVisible = true;
        harness.Frame();
        Assert.Equal(1, paints);
        Assert.Equal(shown, element.ShownTextureHandle);
    }

    [Fact]
    public void AnUnavailableTargetIsReportedOnceAndTheCanvasStaysRegistered()
    {
        var harness = new Harness();
        harness.Device.RenderTargetFailure = _ => new NotSupportedException("no offscreen targets here");
        int paints = 0;
        (PluginCanvasRegistration registration, PluginCanvasElement element) =
            harness.Mount(Hud(), _ => paints++);

        harness.Frame();
        harness.Frame();

        Assert.Equal(0, paints);
        Assert.Equal(0, element.TargetCount);
        Assert.Contains("no offscreen targets here", Assert.Single(harness.Reports), StringComparison.Ordinal);
        Assert.True(registration.IsAvailable);
        Assert.Empty(harness.MainRuns);
    }
}
