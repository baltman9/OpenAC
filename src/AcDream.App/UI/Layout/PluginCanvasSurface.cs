using System.Numerics;
using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;

namespace AcDream.App.UI.Layout;

/// <summary>
/// What the interface needs from the renderer to paint plugin canvases:
/// the device the off-screen targets come from, the frame the passes go
/// into, where the interface shaders are, and the twin resolver the main
/// interface renderer uses. Public so it can travel in the runtime's
/// bindings; the renderer types themselves stay internal.
/// </summary>
public sealed class PluginCanvasHostServices
{
    internal PluginCanvasHostServices(
        IGpuDevice device,
        ICurrentGpuFrameSource frames,
        string shaderDirectory,
        Func<uint, uint>? linearTwinResolver,
        IGpuResourceRetirementQueue? retirement = null)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Frames = frames ?? throw new ArgumentNullException(nameof(frames));
        ArgumentException.ThrowIfNullOrWhiteSpace(shaderDirectory);
        ShaderDirectory = shaderDirectory;
        LinearTwinResolver = linearTwinResolver;
        Retirement = retirement ?? device.Retirement;
    }

    internal IGpuDevice Device { get; }

    internal ICurrentGpuFrameSource Frames { get; }

    internal string ShaderDirectory { get; }

    internal Func<uint, uint>? LinearTwinResolver { get; }

    /// <summary>Where a canvas's textures go when it comes down: after every frame that could be reading them.</summary>
    internal IGpuResourceRetirementQueue Retirement { get; }
}

/// <summary>
/// The one drawing surface every plugin canvas repaints through. The main
/// interface renderer cannot be borrowed for this: a repaint happens while
/// the interface is still collecting its own frame, and would land in the
/// middle of it. So the surface keeps a renderer and a context of its own,
/// begun at the canvas's size for each repaint and flushed into the
/// canvas's off-screen target in a pass of its own, before the interface's
/// pass opens.
///
/// <para>Repaints run one at a time on the interface thread, so one painter
/// serves them all, bound for the duration of each plugin callback and
/// unbound after.</para>
/// </summary>
internal sealed class PluginCanvasSurface : IDisposable
{
    internal const string PassName = "plugin-canvas";

    private readonly PluginCanvasHostServices _services;
    private readonly TextRenderer _renderer;
    private readonly UiRenderContext _context;
    private readonly PluginPainter _painter = new();
    private bool _disposed;

    internal PluginCanvasSurface(PluginCanvasHostServices services, UiDatFont? font)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        Font = font;
        _renderer = new TextRenderer(services.Device, services.Frames, services.ShaderDirectory);
        _renderer.LinearTwinResolver = services.LinearTwinResolver;
        _context = new UiRenderContext(_renderer, Vector2.Zero);
    }

    internal PluginCanvasHostServices Services => _services;

    internal UiDatFont? Font { get; }

    /// <summary>The renderer the repaints collect into; tests read its runs.</summary>
    internal TextRenderer Renderer => _renderer;

    /// <summary>The frame slot in flight, or null between frames, when nothing can be painted.</summary>
    internal int? CurrentFrameSlot => _services.Frames.CurrentFrame?.SlotIndex;

    /// <summary>
    /// Runs one plugin paint callback into a target under the guard. Returns
    /// what the guard returned: true when the callback drew and left the
    /// clip stack as it found it. The target is cleared and drawn either
    /// way, so a callback that threw halfway leaves a blank canvas rather
    /// than half of one over the last.
    /// </summary>
    internal bool Repaint(
        PluginCanvasRegistration registration,
        IGpuRenderTarget target,
        UiDrawCallbackGuard guard,
        PluginImages? images)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(guard);
        int width = registration.Width;
        int height = registration.Height;
        var size = new Vector2(width, height);

        _renderer.Begin(size);
        _context.Begin(size, null);
        _context.PushClip(0f, 0f, width, height);
        _painter.Bind(_context, Font, images, width, height);
        bool drew;
        try
        {
            Action<Plugin.Abstractions.IPluginPainter>? paint = registration.Paint;
            drew = paint is not null && guard.Invoke(_context, _ => paint(_painter));
        }
        finally
        {
            _painter.Unbind();
            _context.PopClip();
        }
        _renderer.FlushTo(target, Vector4.Zero, null, PassName);
        return drew;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderer.Dispose();
    }
}
