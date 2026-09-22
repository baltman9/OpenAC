using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// What the projectile debug overlay does, pinned independently of where it
/// hangs in the tree. These were written against the version that mounted its
/// own panel on the interface root and have to keep holding now that it takes
/// a layer from the shared overlay host instead: the panel and every marker
/// stay click-through, the panel covers the camera viewport, markers are
/// reused rather than re-created, and the whole thing still draws under every
/// real window.
/// </summary>
public sealed class ProjectileDebugOverlayBehaviourPinTests
{
    private const string OverlayName = "PluginProjectileDebugOverlay";
    private static readonly Vector2 Viewport = new(800f, 600f);

    private static Matrix4x4 Projection => Matrix4x4.CreatePerspectiveFieldOfView(
        MathF.PI / 2f, 4f / 3f, 0.1f, 100f);

    private sealed class Fixture
    {
        public required UiRoot Root { get; init; }
        public required ProjectileDebugOverlayController Controller { get; init; }
        public required List<PluginProjectileDebugSample> Samples { get; init; }

        public UiPanel Overlay => FindByName(Root, OverlayName) as UiPanel
            ?? throw new Xunit.Sdk.XunitException(
                $"no element named '{OverlayName}' under the root");
    }

    private static Fixture Build()
    {
        var root = new UiRoot { Width = Viewport.X, Height = Viewport.Y };
        var samples = new List<PluginProjectileDebugSample>();
        ProjectileDebugOverlayController controller =
            ProjectileDebugOverlayController.Mount(
                UiOverlayHost.Mount(root),
                () => samples,
                () => (Matrix4x4.Identity, Projection, Viewport));
        return new Fixture { Root = root, Controller = controller, Samples = samples };
    }

    private static UiElement? FindByName(UiElement from, string name)
    {
        if (from.Name == name) return from;
        foreach (UiElement child in from.Children)
            if (FindByName(child, name) is { } found)
                return found;
        return null;
    }

    /// <summary>The UiRoot child whose subtree contains <paramref name="element"/>.</summary>
    private static UiElement TopLevelAncestorOf(UiRoot root, UiElement element)
    {
        UiElement walk = element;
        while (walk.Parent is { } parent && !ReferenceEquals(parent, root))
            walk = parent;
        Assert.Same(root, walk.Parent);
        return walk;
    }

    [Fact]
    public void ThePanelAndEveryMarkerRefuseInputAndPaintNoChromeOfTheirOwn()
    {
        Fixture fixture = Build();
        fixture.Samples.Add(new PluginProjectileDebugSample(new Vector3(0f, 0f, -10f), true, 0.4f));
        fixture.Controller.Tick();

        UiPanel overlay = fixture.Overlay;
        Assert.True(overlay.ClickThrough);
        Assert.Equal(Vector4.Zero, overlay.BackgroundColor);
        Assert.Equal(Vector4.Zero, overlay.BorderColor);
        Assert.All(overlay.Children, static child => Assert.True(child.ClickThrough));

        // The path from the root down to the overlay must be click-through at
        // every step -- it does not inherit, so one panel that forgot it would
        // swallow every click over the whole viewport.
        UiElement walk = overlay;
        while (walk.Parent is { } parent && parent is not UiRoot)
        {
            Assert.True(parent.ClickThrough, $"'{parent.Name}' swallows input");
            walk = parent;
        }
    }

    [Fact]
    public void ThePanelCoversTheCameraViewport()
    {
        Fixture fixture = Build();
        fixture.Samples.Add(new PluginProjectileDebugSample(new Vector3(0f, 0f, -10f), true, 0.4f));
        fixture.Controller.Tick();

        UiPanel overlay = fixture.Overlay;
        Assert.Equal(0f, overlay.Left);
        Assert.Equal(0f, overlay.Top);
        Assert.Equal(Viewport.X, overlay.Width);
        Assert.Equal(Viewport.Y, overlay.Height);
        Assert.Equal(AnchorEdges.None, overlay.Anchors);
    }

    [Fact]
    public void MarkersTakeTheClearAndBlockedColoursAndAreClampedToTheViewport()
    {
        Fixture fixture = Build();
        fixture.Samples.Add(new PluginProjectileDebugSample(new Vector3(0f, 0f, -10f), true, 0.4f));
        fixture.Samples.Add(new PluginProjectileDebugSample(new Vector3(1f, 0f, -10f), false, 0.4f));
        fixture.Controller.Tick();

        UiPanel overlay = fixture.Overlay;
        Assert.True(overlay.Visible);
        Assert.Equal(2, overlay.Children.Count);
        var clear = (UiPanel)overlay.Children[0];
        var blocked = (UiPanel)overlay.Children[1];
        Assert.Equal(new Vector4(0f, 1f, 0f, 0.95f), clear.BorderColor);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0.95f), blocked.BorderColor);
        Assert.All(
            overlay.Children.Cast<UiPanel>(),
            marker =>
            {
                Assert.True(marker.Visible);
                Assert.Equal(1.5f, marker.BorderThickness);
                Assert.Equal(Vector4.Zero, marker.BackgroundColor);
                Assert.InRange(marker.Left, 0f, Viewport.X);
                Assert.InRange(marker.Top, 0f, Viewport.Y);
                Assert.InRange(marker.Left + marker.Width, 0f, Viewport.X);
                Assert.InRange(marker.Top + marker.Height, 0f, Viewport.Y);
            });
        Assert.InRange(clear.Left, 380f, 400f);
        Assert.InRange(clear.Top, 280f, 300f);
    }

    [Fact]
    public void FewerSamplesHideTheSurplusMarkersInsteadOfDroppingThem()
    {
        Fixture fixture = Build();
        fixture.Samples.Add(new PluginProjectileDebugSample(new Vector3(0f, 0f, -10f), true, 0.4f));
        fixture.Samples.Add(new PluginProjectileDebugSample(new Vector3(1f, 0f, -10f), false, 0.4f));
        fixture.Controller.Tick();
        Assert.Equal(2, fixture.Overlay.Children.Count);

        fixture.Samples.RemoveAt(1);
        fixture.Controller.Tick();

        UiPanel overlay = fixture.Overlay;
        Assert.Equal(2, overlay.Children.Count); // pooled, not re-created
        Assert.True(overlay.Children[0].Visible);
        Assert.False(overlay.Children[1].Visible);
        Assert.True(overlay.Visible);
    }

    [Fact]
    public void NoSamplesHidesThePanelAndEveryMarker()
    {
        Fixture fixture = Build();
        fixture.Samples.Add(new PluginProjectileDebugSample(new Vector3(0f, 0f, -10f), true, 0.4f));
        fixture.Controller.Tick();
        Assert.True(fixture.Overlay.Visible);

        fixture.Samples.Clear();
        fixture.Controller.Tick();

        Assert.False(fixture.Overlay.Visible);
        Assert.All(fixture.Overlay.Children, static child => Assert.False(child.Visible));
    }

    [Fact]
    public void ADegenerateViewportHidesEverythingRatherThanDividingByIt()
    {
        var root = new UiRoot { Width = Viewport.X, Height = Viewport.Y };
        var samples = new List<PluginProjectileDebugSample>
        {
            new(new Vector3(0f, 0f, -10f), true, 0.4f),
        };
        Vector2 viewport = Viewport;
        ProjectileDebugOverlayController controller =
            ProjectileDebugOverlayController.Mount(
                UiOverlayHost.Mount(root),
                () => samples,
                () => (Matrix4x4.Identity, Projection, viewport));

        // Show it first, so a degenerate viewport has something to take back.
        controller.Tick();
        UiElement overlay = Assert.IsAssignableFrom<UiElement>(FindByName(root, OverlayName));
        Assert.True(overlay.Visible);

        viewport = Vector2.Zero;
        controller.Tick();

        Assert.False(overlay.Visible);
        Assert.All(overlay.Children, static child => Assert.False(child.Visible));
    }

    [Fact]
    public void TheOverlayDrawsUnderEveryRealWindow()
    {
        Fixture fixture = Build();
        fixture.Samples.Add(new PluginProjectileDebugSample(new Vector3(0f, 0f, -10f), true, 0.4f));
        fixture.Controller.Tick();

        // Not the band's own floor constant, which would only say the band
        // is where the band says it is: a window keeps the element default
        // unless it sets a z-order, and registering it sets none, so this is
        // the lowest a real window sits and the hardest case.
        var window = new UiPanel { Name = "AWindow" };
        fixture.Root.AddChild(window);
        fixture.Root.RegisterWindow("a-window", window);
        Assert.Equal(new UiPanel().ZOrder, window.ZOrder);

        UiElement overlayBranch = TopLevelAncestorOf(fixture.Root, fixture.Overlay);
        Assert.True(
            overlayBranch.ZOrder < window.ZOrder,
            $"overlay branch z-order {overlayBranch.ZOrder} must stay under {window.ZOrder}");

        UiElement[] backToFront = fixture.Root.ChildrenBackToFrontSnapshot();
        Assert.True(
            Array.IndexOf(backToFront, overlayBranch) < Array.IndexOf(backToFront, window),
            "the overlay must be painted before the window, i.e. underneath it");
    }
}
