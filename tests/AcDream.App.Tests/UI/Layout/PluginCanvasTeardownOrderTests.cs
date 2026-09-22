using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.App.Tests.Rendering.Gpu;
using AcDream.Tests.Fixtures.PluginIcons;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.UI.Layout;

/// <summary>
/// The client's shutdown order, run against a device that behaves like the
/// live one at teardown: the plugin host goes first and drops the plugin's
/// canvases and images, then the interface, then the texture cache, then
/// the device, which drains its retirement ledger only after it has
/// stopped taking requests. Every texture slot a canvas or an image took
/// must be given back exactly once along that path, and nothing may ask
/// the device for anything once it is disposed.
/// </summary>
public sealed class PluginCanvasTeardownOrderTests
{
    private sealed class FrameSource : ICurrentGpuFrameSource
    {
        public IGpuFrame? CurrentFrame { get; set; }
    }

    /// <summary>
    /// The interface's texture services as the live backend shapes them for
    /// a plugin's own art: uploads and releases go through the shared
    /// texture cache. Client art is not part of this ordering.
    /// </summary>
    private sealed class CacheBackedImageBackend(TextureCache cache) : IPluginImageBackend
    {
        public bool TryGetClientArt(uint surfaceId, out uint texture, out int width, out int height)
        {
            texture = 0u; width = 0; height = 0;
            return false;
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

        public uint UploadOwned(byte[] rgba, int width, int height, string debugName) =>
            cache.UploadReleasableRgba8(rgba, width, height, debugName);

        public bool ReleaseOwned(uint texture) => cache.ReleaseUiTexture(texture);
    }

    private sealed class Harness
    {
        public HeldGpuRetirementQueue Flight { get; } = new();
        public RecordingGpuDevice Device { get; }
        public FrameSource Frames { get; } = new();
        public BufferedUiRegistry Registry { get; } = new();
        public UiRoot Root { get; } = new() { Width = 800f, Height = 600f };
        public UiOverlayLayer Layer { get; }
        public PluginCanvasSurface Surface { get; }
        public TextRenderer MainRenderer { get; }
        public UiRenderContext MainContext { get; }
        public TextureCache Cache { get; }
        public PluginUiOwner Owner { get; } = new("example.plugin", "Example");
        private long _now;

        public Harness()
        {
            Device = new RecordingGpuDevice(retirement: Flight);
            Cache = new TextureCache(
                Device,
                dats: null!,
                Device.Retirement,
                Path.Combine(Path.GetTempPath(), "acdream-tests", "canvas-teardown"));
            UiOverlayHost host = UiOverlayHost.Mount(Root);
            Layer = host.AddLayer("PluginCanvases");
            Layer.Visible = true;
            var services = new PluginCanvasHostServices(Device, Frames, "unused", null);
            Surface = new PluginCanvasSurface(services, font: null);
            MainRenderer = new TextRenderer(Device, Frames, "unused");
            MainContext = new UiRenderContext(MainRenderer, new Vector2(800f, 600f));
            Registry.BindImageServices(new CacheBackedImageBackend(Cache));
            Device.Clear();
        }

        public (PluginCanvasRegistration Registration, PluginCanvasElement Element) Mount(
            PluginCanvasDescriptor descriptor, Action<IPluginPainter> paint)
        {
            var registration = (PluginCanvasRegistration)Registry.RegisterCanvas(Owner, descriptor, paint);
            Assert.Single(Registry.DrainCanvases());
            var element = new PluginCanvasElement(registration, Surface, () => Registry.FindImages(Owner));
            Layer.AddChild(element);
            Registry.CompleteCanvasMount(registration, () =>
            {
                Layer.RemoveChild(element);
                element.ReleaseTargets();
            });
            return (registration, element);
        }

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

        /// <summary>
        /// The client's order after the window is asked to close: the plugin
        /// host, then the interface, then the texture cache, then the device.
        /// The plugin's own disposables go the way its scoped registry drops
        /// them, canvases before images.
        /// </summary>
        public void ShutDown(IEnumerable<IDisposable> pluginRegistrations)
        {
            foreach (IDisposable registration in pluginRegistrations)
                registration.Dispose();
            Registry.UnbindCanvasHost();
            Surface.Dispose();
            Registry.UnbindImageServices();
            Cache.Dispose();
            Device.Dispose();
        }

        public uint[] RegisteredSlots =>
            Device.OfKind<GpuRecordedTextureRegistration>().Select(call => call.Slot).ToArray();

        public uint[] ReleasedSlots =>
            Device.OfKind<GpuRecordedTextureRelease>().Select(call => call.Slot).ToArray();
    }

    private static PluginCanvasDescriptor Map() =>
        new("map", 320, 240) { Anchor = PluginCanvasAnchor.Center };

    [Fact]
    public void ACanvasShowingAnImageComesDownInTheClientOrderAndGivesEverySlotBackOnce()
    {
        var harness = new Harness();
        var images = (PluginImages)harness.Registry.ImagesFor(harness.Owner);
        PluginImage art = images.FromStream("map.png", static () => new MemoryStream(PngTestData.Valid()));
        Assert.True(art.IsValid);
        (PluginCanvasRegistration registration, PluginCanvasElement element) = harness.Mount(Map(), painter =>
        {
            painter.FillRect(new PluginRect(0, 0, 320, 240), new PluginColor(0, 0, 0, 160));
            painter.DrawImage(art, new PluginRect(8, 8, 64, 64), PluginColor.White);
        });

        // Painted twice so the canvas holds two targets, one of them shown.
        harness.Frame();
        registration.Invalidate();
        harness.Frame();
        Assert.Equal(2, element.TargetCount);
        uint[] registered = harness.RegisteredSlots;
        // The image and one slot per target.
        Assert.Equal(3, registered.Length);
        Assert.Empty(harness.ReleasedSlots);

        harness.ShutDown([registration, images]);

        // Every slot given back exactly once, none twice, none forgotten.
        Assert.Equal(registered.Order(), harness.ReleasedSlots.Order());
        Assert.Empty(harness.Flight.Pending);
        Assert.All(
            harness.Device.CreatedRenderTargets,
            target => Assert.True(((RecordingGpuTexture)target.ColorTexture).IsDisposed));
        Assert.Equal(0, harness.Cache.ReleasableUiTextureCount);
    }

    [Fact]
    public void APluginsImagesAloneComeDownInTheClientOrderAndGiveTheirSlotsBackOnce()
    {
        var harness = new Harness();
        var images = (PluginImages)harness.Registry.ImagesFor(harness.Owner);
        PluginImage art = images.FromStream("map.png", static () => new MemoryStream(PngTestData.Valid()));
        Assert.True(art.IsValid);
        harness.Frame();
        uint[] registered = harness.RegisteredSlots;
        Assert.Single(registered);

        harness.ShutDown([images]);

        Assert.Equal(registered, harness.ReleasedSlots);
        Assert.Empty(harness.Flight.Pending);
        Assert.Equal(0, harness.Cache.ReleasableUiTextureCount);
    }

    [Fact]
    public void ACanvasReleasedMidSessionWaitsForTheFramesInFlightAndNoLonger()
    {
        var harness = new Harness();
        (PluginCanvasRegistration registration, PluginCanvasElement element) =
            harness.Mount(Map(), painter => painter.Clear(PluginColor.Transparent));
        harness.Frame();
        Assert.Equal(1, element.TargetCount);
        uint[] registered = harness.RegisteredSlots;
        Assert.Single(registered);

        registration.Dispose();

        // The slot is the device's to give back once no frame can be
        // sampling it: held in the flight ledger, not released now.
        Assert.Empty(harness.ReleasedSlots);
        Assert.Single(harness.Flight.Pending);

        harness.Flight.RunAll();

        Assert.Equal(registered, harness.ReleasedSlots);
        Assert.Empty(harness.Flight.Pending);
    }
}
