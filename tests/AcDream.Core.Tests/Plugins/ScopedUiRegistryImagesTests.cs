using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

/// <summary>
/// A plugin's image surface is asked of the host once, under the plugin's
/// own owner, and goes with the plugin: the scoped registry tracks the
/// host's disposable surface like any other registration, so the existing
/// rollback lets the plugin's images go without a new path.
/// </summary>
public sealed class ScopedUiRegistryImagesTests
{
    private sealed class FakeImages : IPluginImages, IDisposable
    {
        public int Disposals { get; private set; }

        public void Dispose() => Disposals++;
    }

    private sealed class FakeScopedUiRegistry : IScopedUiRegistry
    {
        public List<PluginUiOwner> Asked { get; } = [];
        public FakeImages Images { get; } = new();

        public void AddMarkupPanel(string markupPath, object binding)
        {
        }

        public IDisposable RegisterMarkupPanel(string markupPath, object binding) =>
            NoOpUiRegistration.Instance;

        public IPluginImages ImagesFor(PluginUiOwner owner)
        {
            Asked.Add(owner);
            return Images;
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

    [Fact]
    public void TheSurfaceIsAskedOnceUnderThePluginsOwnerAndKept()
    {
        var inner = new FakeScopedUiRegistry();
        var scoped = new ScopedPluginHost(new StubHost(inner), "example.plugin", "Example");

        IPluginImages first = scoped.Ui.Images;
        IPluginImages second = scoped.Ui.Images;

        Assert.Same(inner.Images, first);
        Assert.Same(first, second);
        PluginUiOwner owner = Assert.Single(inner.Asked);
        Assert.Equal(new PluginUiOwner("example.plugin", "Example"), owner);
        scoped.Dispose();
    }

    [Fact]
    public void DisposingThePluginDisposesItsSurface()
    {
        var inner = new FakeScopedUiRegistry();
        var scoped = new ScopedPluginHost(new StubHost(inner), "example.plugin", "Example");
        _ = scoped.Ui.Images;

        scoped.Dispose();

        Assert.Equal(1, inner.Images.Disposals);
        Assert.Throws<ObjectDisposedException>(() => scoped.Ui.Images);
    }

    [Fact]
    public void APluginThatNeverAskedLeavesNothingToDispose()
    {
        var inner = new FakeScopedUiRegistry();
        var scoped = new ScopedPluginHost(new StubHost(inner), "example.plugin", "Example");

        scoped.Dispose();

        Assert.Empty(inner.Asked);
        Assert.Equal(0, inner.Images.Disposals);
    }
}
