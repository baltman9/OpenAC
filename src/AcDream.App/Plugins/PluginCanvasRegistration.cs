using AcDream.Plugin.Abstractions;

namespace AcDream.App.Plugins;

/// <summary>
/// One plugin canvas as the registry holds it: what the plugin asked for,
/// what it has set since, whether it has asked for a repaint, and -- once
/// the interface has mounted it -- how to take it down again. The plugin's
/// handle is this object; the element the interface draws reads it every
/// frame.
///
/// <para>The paint delegate is dropped on dispose. It is the one reference
/// from the client into the plugin's code that the interface holds, and a
/// plugin assembly cannot unload while anything still points into it.</para>
/// </summary>
internal sealed class PluginCanvasRegistration : IPluginCanvas
{
    private readonly BufferedUiRegistry _registry;
    private Action<IPluginPainter>? _paint;
    private volatile bool _invalidated = true;
    private Action? _teardown;

    internal PluginCanvasRegistration(
        BufferedUiRegistry registry,
        long id,
        PluginUiOwner owner,
        PluginCanvasDescriptor descriptor,
        Action<IPluginPainter> paint)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        ArgumentNullException.ThrowIfNull(descriptor);
        _paint = paint ?? throw new ArgumentNullException(nameof(paint));
        Id = id;
        Owner = owner;
        Descriptor = descriptor;
        IsVisible = descriptor.StartVisible;
        Anchor = descriptor.Anchor;
        Offset = descriptor.Offset;
    }

    internal long Id { get; }

    internal PluginUiOwner Owner { get; }

    internal PluginCanvasDescriptor Descriptor { get; }

    /// <summary>Handed to the interface once; the interface mounts each registration a single time.</summary>
    internal bool Drained { get; set; }

    /// <summary>True between a successful mount and teardown.</summary>
    internal bool IsMounted => _teardown is not null;

    /// <summary>
    /// Set by the element when its paint callback was dropped: the canvas
    /// stays registered, so the plugin's handle keeps working, but nothing
    /// is drawn for it again.
    /// </summary>
    internal bool IsDropped { get; set; }

    /// <summary>The plugin's paint callback, or null once the canvas is disposed.</summary>
    internal Action<IPluginPainter>? Paint => _paint;

    public string CanvasId => Descriptor.CanvasId;

    public int Width => Descriptor.Width;

    public int Height => Descriptor.Height;

    public bool IsAvailable => IsMounted && !IsDropped && _paint is not null;

    public bool IsVisible { get; set; }

    public PluginCanvasAnchor Anchor { get; set; }

    public PluginPoint Offset { get; set; }

    public void Invalidate() => _invalidated = true;

    /// <summary>
    /// Takes the pending repaint request, if any, so several invalidations
    /// before one frame paint once.
    /// </summary>
    internal bool TakeInvalidation()
    {
        if (!_invalidated) return false;
        _invalidated = false;
        return true;
    }

    /// <summary>
    /// Records how the interface takes the canvas down: remove the element
    /// from the tree and give its textures back. Runs at once on dispose.
    /// </summary>
    internal void SetTeardown(Action teardown)
    {
        ArgumentNullException.ThrowIfNull(teardown);
        _teardown = teardown;
    }

    /// <summary>
    /// Runs the teardown once and forgets it. The paint delegate stays
    /// unless <paramref name="forget"/> is set: an interface that is going
    /// away hands the canvas back to the registry to be mounted again by
    /// the next one, and the plugin's callback must survive that.
    /// </summary>
    internal void Unmount(bool forget)
    {
        Action? teardown = Interlocked.Exchange(ref _teardown, null);
        if (forget)
            _paint = null;
        teardown?.Invoke();
    }

    public void Dispose() => _registry.RemoveCanvas(this);
}
