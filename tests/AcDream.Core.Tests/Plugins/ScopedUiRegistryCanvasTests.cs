using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

/// <summary>
/// A canvas is registered under the plugin's own owner and goes with the
/// plugin: the scoped registry tracks the host's canvas like any other
/// registration, so disposing the plugin removes it, disposing the canvas
/// removes it once, and every call on the plugin's handle reaches the host's.
/// </summary>
public sealed class ScopedUiRegistryCanvasTests
{
    private sealed class FakeCanvas(PluginCanvasDescriptor descriptor) : IPluginCanvas
    {
        public string CanvasId => descriptor.CanvasId;
        public int Width => descriptor.Width;
        public int Height => descriptor.Height;
        public bool IsAvailable => true;
        public bool IsVisible { get; set; } = descriptor.StartVisible;
        public PluginCanvasAnchor Anchor { get; set; } = descriptor.Anchor;
        public PluginPoint Offset { get; set; } = descriptor.Offset;
        public int Invalidations { get; private set; }
        public int Disposals { get; private set; }
        public int PointerReleases { get; private set; }
        public Action<PluginPointerEvent>? PointerHandler { get; set; }

        public void Invalidate() => Invalidations++;

        public void ReleasePointer() => PointerReleases++;

        public void Dispose() => Disposals++;
    }

    private sealed class FakeScopedUiRegistry : IScopedUiRegistry
    {
        public List<(PluginUiOwner Owner, PluginCanvasDescriptor Descriptor, Action<IPluginPainter> Paint)> Registered { get; } = [];
        public List<FakeCanvas> Canvases { get; } = [];

        public void AddMarkupPanel(string markupPath, object binding)
        {
        }

        public IDisposable RegisterMarkupPanel(string markupPath, object binding) =>
            NoOpUiRegistration.Instance;

        public IPluginCanvas RegisterCanvas(
            PluginUiOwner owner,
            PluginCanvasDescriptor descriptor,
            Action<IPluginPainter> paint)
        {
            Registered.Add((owner, descriptor, paint));
            var canvas = new FakeCanvas(descriptor);
            Canvases.Add(canvas);
            return canvas;
        }
    }

    private sealed class StubHost(IScopedUiRegistry ui) : IPluginHost
    {
        public bool HasUi => true;
        public IPluginLogger Log { get; } = new SilentLogger();
        public IGameState State { get; } = new EmptyGameState();
        public IEvents Events { get; } = new WorldEvents();
        public ISelectionService Selection { get; } = new SelectionState();
        public IUiRegistry Ui { get; } = ui;
        public IAutomationSurface Automation { get; } = NoOpAutomationSurface.Instance;

        private sealed class SilentLogger : IPluginLogger
        {
            public void Info(string message) { }
            public void Warn(string message) { }
            public void Error(string message, Exception? error = null) { }
        }

        private sealed class EmptyGameState : IGameState
        {
            public IReadOnlyList<WorldEntitySnapshot> Entities { get; } = [];
        }
    }

    private static readonly PluginCanvasDescriptor Hud = new("hud", 200, 100)
    {
        Anchor = PluginCanvasAnchor.BottomRight,
        Offset = new PluginPoint(-10, -20),
    };

    [Fact]
    public void RegistrationCarriesTheOwnerTheDescriptorAndTheExactPaintCallback()
    {
        var inner = new FakeScopedUiRegistry();
        var scoped = new ScopedPluginHost(new StubHost(inner), "example.plugin", "Example");
        Action<IPluginPainter> paint = _ => { };

        IPluginCanvas canvas = scoped.Ui.RegisterCanvas(Hud, paint);

        var registered = Assert.Single(inner.Registered);
        Assert.Equal(new PluginUiOwner("example.plugin", "Example"), registered.Owner);
        Assert.Same(Hud, registered.Descriptor);
        Assert.Same(paint, registered.Paint);
        Assert.Equal("hud", canvas.CanvasId);
        Assert.Equal((200, 100), (canvas.Width, canvas.Height));
        Assert.True(canvas.IsAvailable);
        scoped.Dispose();
    }

    [Fact]
    public void EveryCallOnThePluginsHandleReachesTheHostsCanvas()
    {
        var inner = new FakeScopedUiRegistry();
        var scoped = new ScopedPluginHost(new StubHost(inner), "example.plugin", "Example");
        IPluginCanvas canvas = scoped.Ui.RegisterCanvas(Hud, _ => { });
        FakeCanvas hosts = Assert.Single(inner.Canvases);

        canvas.IsVisible = false;
        canvas.Anchor = PluginCanvasAnchor.Center;
        canvas.Offset = new PluginPoint(3, 4);
        canvas.Invalidate();
        canvas.Invalidate();
        Action<PluginPointerEvent> handler = _ => { };
        canvas.PointerHandler = handler;
        canvas.ReleasePointer();

        Assert.Same(handler, hosts.PointerHandler);
        Assert.Same(handler, canvas.PointerHandler);
        Assert.Equal(1, hosts.PointerReleases);
        Assert.False(hosts.IsVisible);
        Assert.Equal(PluginCanvasAnchor.Center, hosts.Anchor);
        Assert.Equal(new PluginPoint(3, 4), hosts.Offset);
        Assert.Equal(2, hosts.Invalidations);
        Assert.False(canvas.IsVisible);
        Assert.Equal(PluginCanvasAnchor.Center, canvas.Anchor);
        scoped.Dispose();
    }

    [Fact]
    public void DisposingThePluginDisposesEveryCanvasItStillHolds()
    {
        var inner = new FakeScopedUiRegistry();
        var scoped = new ScopedPluginHost(new StubHost(inner), "example.plugin", "Example");
        scoped.Ui.RegisterCanvas(Hud, _ => { });
        scoped.Ui.RegisterCanvas(new PluginCanvasDescriptor("map", 64, 64), _ => { });

        scoped.Dispose();

        Assert.All(inner.Canvases, canvas => Assert.Equal(1, canvas.Disposals));
    }

    [Fact]
    public void DisposingTheCanvasRemovesItOnceAndThePluginDoesNotRemoveItAgain()
    {
        var inner = new FakeScopedUiRegistry();
        var scoped = new ScopedPluginHost(new StubHost(inner), "example.plugin", "Example");
        IPluginCanvas canvas = scoped.Ui.RegisterCanvas(Hud, _ => { });
        FakeCanvas hosts = Assert.Single(inner.Canvases);

        canvas.Dispose();
        canvas.Dispose();
        Assert.Equal(1, hosts.Disposals);

        scoped.Dispose();
        Assert.Equal(1, hosts.Disposals);
    }

    [Fact]
    public void ADisposedPluginRefusesANewCanvasAndDisposesWhatTheHostMade()
    {
        var inner = new FakeScopedUiRegistry();
        var scoped = new ScopedPluginHost(new StubHost(inner), "example.plugin", "Example");
        scoped.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scoped.Ui.RegisterCanvas(Hud, _ => { }));
        FakeCanvas made = Assert.Single(inner.Canvases);
        Assert.Equal(1, made.Disposals);
    }
}
