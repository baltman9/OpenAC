using AcDream.App.Plugins;
using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.Plugins;

/// <summary>
/// Canvas registration on the graphical host: capped per plugin, unique
/// by id within a plugin, drained by the interface once, taken down at
/// once when the plugin disposes between the drain and the mount, and
/// handed back to be mounted again when the interface goes away.
/// </summary>
public sealed class BufferedUiRegistryCanvasTests
{
    private sealed class SilentLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? error = null) { }
    }

    private static readonly PluginUiOwner A = new("a", "A");
    private static readonly PluginUiOwner B = new("b", "B");

    private static PluginCanvasDescriptor Canvas(string id) => new(id, 10, 10);

    private static void Paint(IPluginPainter painter)
    {
    }

    [Fact]
    public void ThroughTheHostACanvasIsALiveRegistrationInertUntilMounted()
    {
        var registry = new BufferedUiRegistry();
        IPluginHost host = new AppPluginHost(
            new SilentLogger(), new WorldGameState(), new WorldEvents(), new SelectionState(),
            registry, NoOpAutomationSurface.Instance);

        IPluginCanvas canvas = host.Ui.RegisterCanvas(Canvas("hud"), Paint);

        Assert.IsType<PluginCanvasRegistration>(canvas);
        Assert.False(canvas.IsAvailable);
        Assert.Equal("hud", canvas.CanvasId);
        Assert.True(canvas.IsVisible);
        Assert.True(registry.HasUndrainedCanvases);
        Assert.Equal(1, registry.CanvasCount);
        canvas.Dispose();
        Assert.Equal(0, registry.CanvasCount);
    }

    [Fact]
    public void APluginMayHoldAtMostTheCapAndTheNextIsRefused()
    {
        var registry = new BufferedUiRegistry();
        for (int index = 0; index < BufferedUiRegistry.MaximumCanvasesPerPlugin; index++)
            registry.RegisterCanvas(A, Canvas($"c{index}"), Paint);

        var refused = Assert.Throws<InvalidOperationException>(
            () => registry.RegisterCanvas(A, Canvas("one-more"), Paint));

        Assert.Contains("the most it may", refused.Message, StringComparison.Ordinal);
        // The cap is per plugin.
        Assert.NotNull(registry.RegisterCanvas(B, Canvas("c0"), Paint));
    }

    [Fact]
    public void ACanvasIdIsUniqueWithinOnePluginNotAcrossPlugins()
    {
        var registry = new BufferedUiRegistry();
        registry.RegisterCanvas(A, Canvas("hud"), Paint);

        Assert.Throws<InvalidOperationException>(() => registry.RegisterCanvas(A, Canvas("hud"), Paint));
        Assert.NotNull(registry.RegisterCanvas(B, Canvas("hud"), Paint));
    }

    [Fact]
    public void ADisposedCanvasFreesItsIdAndItsPlaceUnderTheCap()
    {
        var registry = new BufferedUiRegistry();
        IPluginCanvas first = registry.RegisterCanvas(A, Canvas("hud"), Paint);
        first.Dispose();

        Assert.NotNull(registry.RegisterCanvas(A, Canvas("hud"), Paint));
    }

    [Fact]
    public void EachRegistrationIsDrainedOnce()
    {
        var registry = new BufferedUiRegistry();
        registry.RegisterCanvas(A, Canvas("hud"), Paint);

        Assert.Single(registry.DrainCanvases());
        Assert.Empty(registry.DrainCanvases());
        Assert.False(registry.HasUndrainedCanvases);

        registry.RegisterCanvas(A, Canvas("map"), Paint);
        Assert.True(registry.HasUndrainedCanvases);
        Assert.Equal("map", Assert.Single(registry.DrainCanvases()).CanvasId);
    }

    [Fact]
    public void ACanvasDisposedBetweenDrainAndMountIsTornDownAtOnce()
    {
        var registry = new BufferedUiRegistry();
        IPluginCanvas canvas = registry.RegisterCanvas(A, Canvas("hud"), Paint);
        PluginCanvasRegistration drained = Assert.Single(registry.DrainCanvases());
        canvas.Dispose();
        int teardowns = 0;

        registry.CompleteCanvasMount(drained, () => teardowns++);

        Assert.Equal(1, teardowns);
        Assert.False(drained.IsMounted);
        Assert.Null(drained.Paint);
    }

    [Fact]
    public void AMountedCanvasIsTornDownOnceOnDisposeAndItsPaintDelegateDropped()
    {
        var registry = new BufferedUiRegistry();
        IPluginCanvas canvas = registry.RegisterCanvas(A, Canvas("hud"), Paint);
        PluginCanvasRegistration drained = Assert.Single(registry.DrainCanvases());
        int teardowns = 0;
        registry.CompleteCanvasMount(drained, () => teardowns++);
        Assert.True(drained.IsMounted);
        Assert.True(drained.IsAvailable);

        canvas.Dispose();
        canvas.Dispose();

        Assert.Equal(1, teardowns);
        Assert.Null(drained.Paint);
        Assert.False(drained.IsAvailable);
        Assert.Equal(0, registry.CanvasCount);
    }

    [Fact]
    public void AFailedMountRemovesTheRegistration()
    {
        var registry = new BufferedUiRegistry();
        IPluginCanvas canvas = registry.RegisterCanvas(A, Canvas("hud"), Paint);
        PluginCanvasRegistration drained = Assert.Single(registry.DrainCanvases());

        registry.FailCanvasMount(drained);

        Assert.Equal(0, registry.CanvasCount);
        Assert.False(canvas.IsAvailable);
        canvas.Dispose();
    }

    [Fact]
    public void WhenTheInterfaceGoesAwayCanvasesComeDownAndWaitForTheNextOneWithTheirCallbacks()
    {
        var registry = new BufferedUiRegistry();
        IPluginCanvas canvas = registry.RegisterCanvas(A, Canvas("hud"), Paint);
        PluginCanvasRegistration drained = Assert.Single(registry.DrainCanvases());
        int teardowns = 0;
        registry.CompleteCanvasMount(drained, () => teardowns++);
        drained.IsDropped = true;

        registry.UnbindCanvasHost();

        Assert.Equal(1, teardowns);
        Assert.False(drained.IsMounted);
        Assert.False(drained.IsDropped);
        Assert.NotNull(drained.Paint);
        Assert.True(registry.HasUndrainedCanvases);
        Assert.Same(drained, Assert.Single(registry.DrainCanvases()));
        Assert.Equal(1, registry.CanvasCount);
        canvas.Dispose();
        Assert.Equal(1, teardowns);
    }

    [Fact]
    public void InvalidationIsTakenOnceHoweverManyTimesItWasAsked()
    {
        var registry = new BufferedUiRegistry();
        var registration = (PluginCanvasRegistration)registry.RegisterCanvas(A, Canvas("hud"), Paint);

        // A new canvas starts invalidated: it has to be painted once to show anything.
        Assert.True(registration.TakeInvalidation());
        Assert.False(registration.TakeInvalidation());
        registration.Invalidate();
        registration.Invalidate();
        Assert.True(registration.TakeInvalidation());
        Assert.False(registration.TakeInvalidation());
    }
}
