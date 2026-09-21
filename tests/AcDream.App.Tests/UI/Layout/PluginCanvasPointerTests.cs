using System.Collections.Generic;
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
/// Pointer input on a plugin canvas: a canvas that opted in and has a
/// handler is hit-testable inside its own rectangle and nowhere else,
/// hands the plugin canvas-local pixels whatever its anchor, offset or
/// interface scale, holds the pointer through a drag that leaves the
/// rectangle, gets the wheel only when the pointer is over it, and is
/// dropped back to click-through when its handler misbehaves. A canvas
/// that did not opt in is untouched by all of it.
/// </summary>
public sealed class PluginCanvasPointerTests
{
    private sealed class FrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame { get; set; }
    }

    private sealed class HeldRetirementQueue : IGpuResourceRetirementQueue
    {
        public void Retire(Action release)
        {
        }
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

    private sealed class Harness
    {
        public RecordingGpuDevice Device { get; } = new();
        public BufferedUiRegistry Registry { get; } = new();
        public UiRoot Root { get; } = new() { Width = 800f, Height = 600f };
        public UiOverlayLayer Layer { get; }
        public PluginCanvasSurface Surface { get; }
        public List<string> Reports { get; } = [];
        public ManualClock Clock { get; } = new();
        public PluginKeyModifiers Modifiers { get; set; }
        public PluginUiOwner Owner { get; } = new("example.plugin", "Example");
        public List<(UiMouseButton Button, int X, int Y)> WorldPresses { get; } = [];
        public int WorldScrolls { get; private set; }
        private long _now;

        public Harness()
        {
            UiOverlayHost host = UiOverlayHost.Mount(Root);
            Layer = host.AddLayer("PluginCanvases");
            Layer.Visible = true;
            var services = new PluginCanvasHostServices(Device, new FrameSource(), "unused", null, new HeldRetirementQueue());
            Surface = new PluginCanvasSurface(services, font: null);
            Root.WorldMouseFallThrough += (button, x, y, _) => WorldPresses.Add((button, x, y));
            Root.WorldScrollFallThrough += _ => WorldScrolls++;
        }

        public (PluginCanvasRegistration Registration, PluginCanvasElement Element) Mount(
            PluginCanvasDescriptor descriptor, Action<PluginPointerEvent>? handler)
        {
            var registration = (PluginCanvasRegistration)Registry.RegisterCanvas(Owner, descriptor, _ => { });
            IPluginCanvas canvas = registration;
            canvas.PointerHandler = handler;
            Assert.Single(Registry.DrainCanvases());
            var element = new PluginCanvasElement(
                registration, Surface, () => Registry.FindImages(Owner), Reports.Add, Clock.Read,
                () => Modifiers);
            Layer.AddChild(element, takesInput: descriptor.AcceptsPointerInput);
            Registry.CompleteCanvasMount(registration, () =>
            {
                Layer.RemoveChild(element);
                element.ReleaseTargets();
            });
            Tick();
            return (registration, element);
        }

        public void Tick()
        {
            _now += 16;
            Root.Tick(0.016, _now);
        }
    }

    private static PluginCanvasDescriptor Map(bool input = true, PluginCanvasAnchor anchor = PluginCanvasAnchor.TopLeft) =>
        new("map", 200, 100)
        {
            Anchor = anchor,
            Offset = new PluginPoint(10, 20),
            AcceptsPointerInput = input,
        };

    private static (Harness Harness, PluginCanvasRegistration Registration, PluginCanvasElement Element, List<PluginPointerEvent> Events)
        MountedMap(bool input = true, PluginCanvasAnchor anchor = PluginCanvasAnchor.TopLeft)
    {
        var harness = new Harness();
        var events = new List<PluginPointerEvent>();
        (PluginCanvasRegistration registration, PluginCanvasElement element) = harness.Mount(Map(input, anchor), events.Add);
        return (harness, registration, element, events);
    }

    [Fact]
    public void AnInputCanvasIsHitInsideItsRectangleAndNowhereElse()
    {
        (Harness harness, _, PluginCanvasElement element, _) = MountedMap();

        Assert.Same(element, harness.Root.Pick(10, 20));
        Assert.Same(element, harness.Root.Pick(209, 119));
        Assert.Null(harness.Root.Pick(9, 20));
        Assert.Null(harness.Root.Pick(210, 119));
        Assert.Null(harness.Root.Pick(100, 120));
        Assert.Null(harness.Root.Pick(400, 300));
    }

    [Fact]
    public void AClickThroughCanvasIsUnchangedEvenWithAHandlerSet()
    {
        (Harness harness, _, _, List<PluginPointerEvent> events) = MountedMap(input: false);

        Assert.Null(harness.Root.Pick(50, 50));
        harness.Root.OnMouseDown(UiMouseButton.Left, 50, 50);
        harness.Root.OnMouseMove(60, 60);
        harness.Root.OnMouseUp(UiMouseButton.Left, 60, 60);
        harness.Root.OnScroll(1);

        Assert.Empty(events);
        Assert.Equal([(UiMouseButton.Left, 50, 50), (UiMouseButton.Left, 60, 60)], harness.WorldPresses);
        Assert.Equal(1, harness.WorldScrolls);
    }

    [Fact]
    public void AnInputCanvasWithoutAHandlerStaysClickThrough()
    {
        var harness = new Harness();
        harness.Mount(Map(), handler: null);

        Assert.Null(harness.Root.Pick(50, 50));
        harness.Root.OnMouseDown(UiMouseButton.Left, 50, 50);
        Assert.Single(harness.WorldPresses);
    }

    [Theory]
    [InlineData(PluginCanvasAnchor.TopLeft, 10, 20)]
    [InlineData(PluginCanvasAnchor.TopCenter, 310, 20)]
    [InlineData(PluginCanvasAnchor.TopRight, 610, 20)]
    [InlineData(PluginCanvasAnchor.CenterLeft, 10, 270)]
    [InlineData(PluginCanvasAnchor.Center, 310, 270)]
    [InlineData(PluginCanvasAnchor.CenterRight, 610, 270)]
    [InlineData(PluginCanvasAnchor.BottomLeft, 10, 520)]
    [InlineData(PluginCanvasAnchor.BottomCenter, 310, 520)]
    [InlineData(PluginCanvasAnchor.BottomRight, 610, 520)]
    public void APressArrivesInCanvasPixelsUnderEveryAnchor(PluginCanvasAnchor anchor, int left, int top)
    {
        (Harness harness, _, PluginCanvasElement element, List<PluginPointerEvent> events) = MountedMap(anchor: anchor);
        Assert.Equal((left, top), ((int)element.Left, (int)element.Top));

        harness.Root.OnMouseDown(UiMouseButton.Left, left + 5, top + 7);
        harness.Root.OnMouseUp(UiMouseButton.Left, left + 5, top + 7);

        Assert.Equal(2, events.Count);
        Assert.Equal(new PluginPointerEvent(PluginPointerEventKind.Down, new PluginPoint(5, 7), PluginPointerButton.Left, PluginKeyModifiers.None), events[0]);
        Assert.Equal(new PluginPointerEvent(PluginPointerEventKind.Up, new PluginPoint(5, 7), PluginPointerButton.Left, PluginKeyModifiers.None), events[1]);
        Assert.Null(harness.Root.Pick(left - 1, top - 1));
    }

    [Fact]
    public void WindowCoordinatesAreScaledIntoCanvasPixelsWhenTheInterfaceIsScaled()
    {
        (Harness harness, _, _, List<PluginPointerEvent> events) = MountedMap();
        // The interface is laid out at 400x300 and shown stretched over the 800x600 window.
        harness.Root.DeclareFixedCanvas(this, new Vector2(400f, 300f));
        harness.Tick();

        harness.Root.OnMouseDown(UiMouseButton.Left, 2 * (10 + 5), 2 * (20 + 7));
        harness.Root.OnMouseUp(UiMouseButton.Left, 2 * (10 + 5), 2 * (20 + 7));

        Assert.Equal(new PluginPoint(5, 7), events[0].Position);
        Assert.Equal(new PluginPoint(5, 7), events[1].Position);
    }

    [Fact]
    public void ADragKeepsThePointerAfterItLeavesTheRectangle()
    {
        (Harness harness, _, PluginCanvasElement element, List<PluginPointerEvent> events) = MountedMap();

        harness.Root.OnMouseDown(UiMouseButton.Left, 100, 60);
        Assert.Same(element, harness.Root.Captured);
        harness.Root.OnMouseMove(150, 80);
        harness.Root.OnMouseMove(400, 300);   // far outside, in one jump
        harness.Root.OnMouseUp(UiMouseButton.Left, 400, 300);

        Assert.Equal(
            [PluginPointerEventKind.Down, PluginPointerEventKind.Move, PluginPointerEventKind.Move, PluginPointerEventKind.Up],
            events.ConvertAll(e => e.Kind));
        Assert.Equal(new PluginPoint(90, 40), events[0].Position);
        Assert.Equal(new PluginPoint(140, 60), events[1].Position);
        Assert.Equal(new PluginPoint(390, 280), events[2].Position);
        Assert.Equal(new PluginPoint(390, 280), events[3].Position);
        Assert.All(events, e => Assert.Equal(PluginPointerButton.Left, e.Button));
        Assert.Null(harness.Root.Captured);
        Assert.Empty(harness.WorldPresses);
    }

    [Fact]
    public void AMoveWithNoButtonHeldIsNotReported()
    {
        (Harness harness, _, _, List<PluginPointerEvent> events) = MountedMap();

        harness.Root.OnMouseMove(100, 60);
        harness.Root.OnMouseMove(120, 70);

        Assert.Empty(events);
    }

    [Fact]
    public void TheRightAndMiddleButtonsDragToo()
    {
        (Harness harness, _, _, List<PluginPointerEvent> events) = MountedMap();

        harness.Root.OnMouseDown(UiMouseButton.Right, 100, 60);
        harness.Root.OnMouseMove(110, 60);
        harness.Root.OnMouseUp(UiMouseButton.Right, 110, 60);
        harness.Root.OnMouseDown(UiMouseButton.Middle, 100, 60);
        harness.Root.OnMouseUp(UiMouseButton.Middle, 100, 60);

        Assert.Equal(
            [PluginPointerButton.Right, PluginPointerButton.Right, PluginPointerButton.Right, PluginPointerButton.Middle, PluginPointerButton.Middle],
            events.ConvertAll(e => e.Button));
        Assert.Equal(PluginPointerEventKind.Move, events[1].Kind);
    }

    [Fact]
    public void TheWheelReachesTheCanvasOnlyWhileThePointerIsOverIt()
    {
        (Harness harness, _, _, List<PluginPointerEvent> events) = MountedMap();

        harness.Root.OnMouseMove(100, 60);
        harness.Root.OnScroll(3);
        harness.Root.OnMouseMove(400, 300);
        harness.Root.OnScroll(-1);

        PluginPointerEvent wheel = Assert.Single(events);
        Assert.Equal(PluginPointerEventKind.Wheel, wheel.Kind);
        Assert.Equal(3, wheel.WheelDelta);
        Assert.Equal(new PluginPoint(90, 40), wheel.Position);
        Assert.Equal(PluginPointerButton.None, wheel.Button);
        Assert.Equal(1, harness.WorldScrolls);
    }

    [Fact]
    public void TheModifierKeysHeldTravelWithEveryEvent()
    {
        (Harness harness, _, _, List<PluginPointerEvent> events) = MountedMap();

        harness.Modifiers = PluginKeyModifiers.Shift;
        harness.Root.OnMouseDown(UiMouseButton.Left, 100, 60);
        harness.Modifiers = PluginKeyModifiers.Shift | PluginKeyModifiers.Control;
        harness.Root.OnMouseMove(110, 60);
        harness.Modifiers = PluginKeyModifiers.Alt;
        harness.Root.OnMouseUp(UiMouseButton.Left, 110, 60);
        harness.Root.OnScroll(1);

        Assert.Equal(
            [PluginKeyModifiers.Shift, PluginKeyModifiers.Shift | PluginKeyModifiers.Control, PluginKeyModifiers.Alt, PluginKeyModifiers.Alt],
            events.ConvertAll(e => e.Modifiers));
    }

    [Fact]
    public void ReleasingThePointerFromInsideTheHandlerEndsTheDragWithoutACancel()
    {
        var harness = new Harness();
        var events = new List<PluginPointerEvent>();
        IPluginCanvas? canvas = null;
        (PluginCanvasRegistration registration, _) = harness.Mount(Map(), e =>
        {
            events.Add(e);
            if (e.Kind == PluginPointerEventKind.Down) canvas!.ReleasePointer();
        });
        canvas = registration;

        harness.Root.OnMouseDown(UiMouseButton.Left, 100, 60);
        Assert.Null(harness.Root.Captured);
        harness.Root.OnMouseMove(110, 60);
        harness.Root.OnMouseUp(UiMouseButton.Left, 110, 60);

        PluginPointerEvent only = Assert.Single(events);
        Assert.Equal(PluginPointerEventKind.Down, only.Kind);
        // The release went to the world, as any release with nothing captured does.
        Assert.Single(harness.WorldPresses);
    }

    [Fact]
    public void HidingTheCanvasMidDragCancelsThePress()
    {
        (Harness harness, PluginCanvasRegistration registration, _, List<PluginPointerEvent> events) = MountedMap();

        harness.Root.OnMouseDown(UiMouseButton.Left, 100, 60);
        registration.IsVisible = false;
        harness.Tick();
        harness.Root.OnMouseMove(110, 60);
        harness.Root.OnMouseUp(UiMouseButton.Left, 110, 60);

        Assert.Equal([PluginPointerEventKind.Down, PluginPointerEventKind.Cancelled], events.ConvertAll(e => e.Kind));
        Assert.Equal(PluginPointerButton.Left, events[1].Button);
        Assert.Null(harness.Root.Captured);
    }

    [Fact]
    public void AThrowingHandlerIsDroppedAndTheCanvasGoesBackToClickThroughStillPainting()
    {
        var harness = new Harness();
        int calls = 0;
        (PluginCanvasRegistration registration, PluginCanvasElement element) = harness.Mount(Map(), _ =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        });

        harness.Root.OnMouseDown(UiMouseButton.Left, 100, 60);
        harness.Root.OnMouseMove(110, 60);
        harness.Root.OnMouseUp(UiMouseButton.Left, 110, 60);
        harness.Root.OnMouseDown(UiMouseButton.Left, 100, 60);

        Assert.Equal(1, calls);
        string report = Assert.Single(harness.Reports);
        Assert.Contains("boom", report);
        Assert.Contains("pointer", report);
        Assert.True(element.IsInputDropped);
        Assert.Null(harness.Root.Captured);
        Assert.Null(harness.Root.Pick(100, 60));
        // The release after the drop and the second press both went to the world.
        Assert.Equal(2, harness.WorldPresses.Count);
        Assert.False(registration.IsDropped);
        Assert.True(element.Visible);
    }

    [Fact]
    public void ASlowHandlerIsDroppedAfterThreeOverrunsInARow()
    {
        var harness = new Harness();
        int calls = 0;
        (_, PluginCanvasElement element) = harness.Mount(Map(), _ => calls++);
        harness.Clock.NextCallCosts = UiDrawCallbackGuard.FrameBudgetMilliseconds + 1.0;

        harness.Root.OnMouseDown(UiMouseButton.Left, 100, 60);
        harness.Root.OnMouseMove(101, 60);
        Assert.False(element.IsInputDropped);
        harness.Root.OnMouseMove(102, 60);
        Assert.True(element.IsInputDropped);
        harness.Root.OnMouseMove(103, 60);
        harness.Root.OnMouseUp(UiMouseButton.Left, 103, 60);

        Assert.Equal(3, calls);
        Assert.Contains("3 events in a row", Assert.Single(harness.Reports));
        Assert.Null(harness.Root.Captured);
    }

    [Fact]
    public void DisposingTheCanvasDropsTheHandlerAndReleasesThePointer()
    {
        (Harness harness, PluginCanvasRegistration registration, _, List<PluginPointerEvent> events) = MountedMap();

        harness.Root.OnMouseDown(UiMouseButton.Left, 100, 60);
        registration.Dispose();
        harness.Root.OnMouseUp(UiMouseButton.Left, 100, 60);

        Assert.Null(registration.PointerHandler);
        Assert.Null(harness.Root.Captured);
        Assert.Equal([PluginPointerEventKind.Down, PluginPointerEventKind.Cancelled], events.ConvertAll(e => e.Kind));
    }
}
