using System.Threading;
using System.Collections.Generic;
using AcDream.App.Plugins;
using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;
using AcDream.Tests.Fixtures.PluginIcons;

namespace AcDream.App.Tests.Plugins;

/// <summary>
/// The graphical host's image surface, reached the way a plugin reaches it:
/// through <see cref="IPluginHost.Ui"/>. Inert until the interface binds
/// its texture services, live after, one table per plugin, and let go when
/// the plugin's surface is disposed or the interface goes away.
/// </summary>
public sealed class BufferedUiRegistryImagesTests
{
    private sealed class FakeBackend : IPluginImageBackend
    {
        private uint _next = 50u;
        public List<uint> AskedArt { get; } = [];
        public List<uint> Released { get; } = [];
        public int Uploads { get; private set; }

        public bool TryGetClientArt(uint surfaceId, out uint texture, out int width, out int height)
        {
            AskedArt.Add(surfaceId);
            texture = 7u; width = 16; height = 16;
            return true;
        }

        public bool TryGetSpellIcon(uint spellId, out uint texture, out int width, out int height)
        {
            texture = 8u; width = 32; height = 32;
            return true;
        }

        public bool TryGetObjectIcon(uint objectId, out uint texture, out int width, out int height)
        {
            texture = 9u; width = 32; height = 32;
            return true;
        }

        public uint UploadOwned(byte[] rgba, int width, int height, string debugName)
        {
            Uploads++;
            return _next++;
        }

        public bool ReleaseOwned(uint texture)
        {
            Released.Add(texture);
            return true;
        }
    }

    private sealed class SilentLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? error = null) { }
    }

    private static (IPluginHost Host, BufferedUiRegistry Registry) Host()
    {
        var registry = new BufferedUiRegistry();
        var host = new AppPluginHost(
            new SilentLogger(),
            new WorldGameState(),
            new WorldEvents(),
            new SelectionState(),
            registry,
            NoOpAutomationSurface.Instance);
        return (host, registry);
    }

    private static readonly PluginUiOwner Unscoped = new("unscoped", "Plugin");

    [Fact]
    public void ThroughTheHostTheSurfaceIsInertUntilTheInterfaceBindsAndLiveAfter()
    {
        (IPluginHost host, BufferedUiRegistry registry) = Host();
        IPluginImages images = host.Ui.Images;

        Assert.NotSame(NoOpPluginImages.Instance, images);
        Assert.False(images.IsAvailable);
        Assert.Equal(PluginImage.None, images.FromClientArt(0x1234u));
        Assert.Equal(PluginImageBudget.Default.MaximumCount, images.MaximumCount);
        Assert.Equal(PluginImageBudget.Default.MaximumBytes, images.MaximumBytes);
        Assert.Equal(PluginImageBudget.Default.MaximumDimension, images.MaximumDimension);

        var backend = new FakeBackend();
        registry.BindImageServices(backend);

        Assert.True(images.IsAvailable);
        PluginImage art = images.FromClientArt(0x1234u);
        Assert.True(art.IsValid);
        Assert.Equal((16, 16), (art.Width, art.Height));
        // A bare index is normalised into the image block, as icon ids are.
        Assert.Equal([0x06001234u], backend.AskedArt);
        Assert.Same(images, host.Ui.Images);
    }

    [Fact]
    public void ATableMadeAfterBindingIsBoundAtOnce()
    {
        (_, BufferedUiRegistry registry) = Host();
        registry.BindImageServices(new FakeBackend());

        IPluginImages images = registry.ImagesFor(new PluginUiOwner("late.plugin", "Late"));

        Assert.True(images.IsAvailable);
        Assert.True(images.FromSpellIcon(1u).IsValid);
    }

    /// <summary>
    /// The interface thread is the one the services were bound from, whichever
    /// thread a plugin first asks for its images on. A plugin whose first
    /// ask is from a worker must not make the worker the accepted thread and
    /// the interface the refused one; its uploads would then leave the
    /// render thread.
    /// </summary>
    [Fact]
    public void ATableFirstAskedForOnAWorkerThreadStillAnswersOnlyTheInterfaceThread()
    {
        (_, BufferedUiRegistry registry) = Host();
        registry.BindImageServices(new FakeBackend());
        var owner = new PluginUiOwner("worker.plugin", "Worker");
        IPluginImages? madeOnWorker = null;
        Exception? workerFailure = null;

        var worker = new Thread(() =>
        {
            try
            {
                madeOnWorker = registry.ImagesFor(owner);
                madeOnWorker.FromSpellIcon(1u);
            }
            catch (Exception caught)
            {
                workerFailure = caught;
            }
        });
        worker.Start();
        worker.Join();

        Assert.NotNull(madeOnWorker);
        Assert.IsType<InvalidOperationException>(workerFailure);
        IPluginImages images = registry.ImagesFor(owner);
        Assert.Same(madeOnWorker, images);
        Assert.True(images.FromSpellIcon(1u).IsValid);
        Assert.Equal(1, images.Count);
    }

    [Fact]
    public void EachPluginHasItsOwnTableAndTheSameOneOnEveryCall()
    {
        (_, BufferedUiRegistry registry) = Host();
        registry.BindImageServices(new FakeBackend());

        IPluginImages a = registry.ImagesFor(new PluginUiOwner("a", "A"));
        IPluginImages b = registry.ImagesFor(new PluginUiOwner("b", "B"));

        Assert.NotSame(a, b);
        Assert.Same(a, registry.ImagesFor(new PluginUiOwner("a", "A")));
        a.FromClientArt(1u);
        Assert.Equal(1, a.Count);
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void DisposingThePluginsSurfaceGivesBackItsArtAndTheNextAskIsFresh()
    {
        (_, BufferedUiRegistry registry) = Host();
        var backend = new FakeBackend();
        registry.BindImageServices(backend);
        var owner = new PluginUiOwner("a", "A");
        IPluginImages images = registry.ImagesFor(owner);
        PluginImage own = images.FromStream("map.png", static () => new MemoryStream(PngTestData.Valid()));
        Assert.True(own.IsValid);

        ((IDisposable)images).Dispose();

        Assert.Single(backend.Released);
        Assert.False(images.IsAvailable);
        Assert.Equal(PluginImage.None, images.FromClientArt(1u));
        Assert.False(images.Release(own));
        IPluginImages fresh = registry.ImagesFor(owner);
        Assert.NotSame(images, fresh);
        Assert.True(fresh.IsAvailable);
    }

    [Fact]
    public void UnbindingTheInterfaceLetsEveryPluginsArtGoAndAnswersNoneUntilRebound()
    {
        (IPluginHost host, BufferedUiRegistry registry) = Host();
        var backend = new FakeBackend();
        registry.BindImageServices(backend);
        IPluginImages images = host.Ui.Images;
        PluginImage own = images.FromStream("map.png", static () => new MemoryStream(PngTestData.Valid()));
        PluginImage art = images.FromClientArt(1u);
        Assert.True(own.IsValid && art.IsValid);

        registry.UnbindImageServices();

        Assert.Single(backend.Released);
        Assert.False(images.IsAvailable);
        Assert.Equal(0, images.Count);
        Assert.False(images.Release(art));
        Assert.Equal(PluginImage.None, images.FromClientArt(1u));
        Assert.NotNull(registry.FindImages(Unscoped));

        var rebound = new FakeBackend();
        registry.BindImageServices(rebound);
        Assert.True(images.IsAvailable);
        Assert.True(images.FromClientArt(1u).IsValid);
        Assert.Equal([0x06000001u], rebound.AskedArt);
    }

    [Fact]
    public void UnbindTwiceAndBindTwiceBehave()
    {
        (_, BufferedUiRegistry registry) = Host();
        registry.UnbindImageServices();
        registry.BindImageServices(new FakeBackend());

        Assert.Throws<InvalidOperationException>(() => registry.BindImageServices(new FakeBackend()));
        registry.UnbindImageServices();
        registry.UnbindImageServices();
    }
}
