using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Plugins;
using AcDream.App.Rendering;
using AcDream.App.Rendering.Gpu;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.UI.Layout;

/// <summary>
/// One plugin canvas in the interface tree: a rectangle on the shared
/// overlay layer that shows an off-screen texture the plugin painted.
///
/// <para>The texture is retained. Each frame the element asks the
/// registration whether the plugin invalidated; if so, and a frame is
/// open, it paints once through the shared surface into a target of its
/// own and shows that. Otherwise it shows what it showed last. A canvas
/// that is never invalidated costs one quad a frame.</para>
///
/// <para>Targets are pooled the way the creature viewport's are: a target
/// is written only when no frame still in flight can be reading it, which
/// with the frame flight's guarantee means one last used in the slot the
/// current frame occupies. The target being shown is never the one being
/// painted, so a repaint never tears. The pool grows to the flight depth
/// plus one and no further.</para>
/// </summary>
internal sealed class PluginCanvasElement : UiElement
{
    /// <summary>
    /// How long one repaint may take. Twice the frame guard's budget: a
    /// repaint is a whole map or HUD, not a strip of an existing frame, and
    /// it happens when the plugin asks rather than every frame.
    /// </summary>
    internal const double RepaintBudgetMilliseconds = 4.0;

    /// <summary>The most targets a canvas may hold; the flight depth plus one is the real bound.</summary>
    internal const int MaximumTargets = 8;

    private sealed class CanvasTarget(IGpuRenderTarget target, GpuTextureSlot slot)
    {
        internal IGpuRenderTarget Target { get; } = target;
        internal GpuTextureSlot Slot { get; } = slot;
        internal uint Handle => UiTextureTableHandle.FromSlot(Slot);
        internal int LastUsedFrameSlot { get; set; } = -1;
        internal bool HasContent { get; set; }
    }

    private readonly PluginCanvasRegistration _registration;
    private readonly PluginCanvasSurface _surface;
    private readonly Func<PluginImages?> _images;
    private readonly UiDrawCallbackGuard _guard;
    private readonly Action<string> _report;
    private readonly List<CanvasTarget> _targets = [];
    private CanvasTarget? _shown;
    private bool _targetsUnavailable;
    private bool _released;

    internal PluginCanvasElement(
        PluginCanvasRegistration registration,
        PluginCanvasSurface surface,
        Func<PluginImages?> images,
        Action<string>? report = null,
        Func<double>? nowMilliseconds = null)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _report = report ?? (line => Serilog.Log.Warning("{Line}", line));
        _guard = new UiDrawCallbackGuard(
            $"plugin canvas {registration.Owner.Id}/{registration.CanvasId}",
            _report,
            nowMilliseconds,
            RepaintBudgetMilliseconds);
        Name = $"PluginCanvas:{registration.Owner.Id}:{registration.CanvasId}";
        ClickThrough = true;
        Anchors = AnchorEdges.None;
        Width = registration.Width;
        Height = registration.Height;
        // Read by the tree before the visibility check, so a canvas the
        // plugin hid and shows again is ticked and drawn on the next frame.
        VisibleSource = () => _registration.IsVisible && !_registration.IsDropped && !_released;
        Visible = VisibleSource();
    }

    internal PluginCanvasRegistration Registration => _registration;

    internal UiDrawCallbackGuard Guard => _guard;

    internal int TargetCount => _targets.Count;

    /// <summary>The interface texture currently shown, or 0 before the first paint.</summary>
    internal uint ShownTextureHandle => _shown is { HasContent: true } shown ? shown.Handle : 0u;

    protected override bool ClipsChildren => true;

    protected override void OnTick(double deltaSeconds) => Layout();

    /// <summary>
    /// Places the canvas by anchor plus offset inside its layer, which the
    /// overlay host keeps equal to the viewport. Re-run every tick because
    /// the plugin may move the canvas at any time and the viewport can
    /// change size.
    /// </summary>
    internal void Layout()
    {
        float layerWidth = Parent?.Width ?? 0f;
        float layerHeight = Parent?.Height ?? 0f;
        float x = (float)_registration.Offset.X;
        float y = (float)_registration.Offset.Y;
        switch (_registration.Anchor)
        {
            case PluginCanvasAnchor.TopCenter:
            case PluginCanvasAnchor.Center:
            case PluginCanvasAnchor.BottomCenter:
                x += (layerWidth - Width) * 0.5f;
                break;
            case PluginCanvasAnchor.TopRight:
            case PluginCanvasAnchor.CenterRight:
            case PluginCanvasAnchor.BottomRight:
                x += layerWidth - Width;
                break;
        }
        switch (_registration.Anchor)
        {
            case PluginCanvasAnchor.CenterLeft:
            case PluginCanvasAnchor.Center:
            case PluginCanvasAnchor.CenterRight:
                y += (layerHeight - Height) * 0.5f;
                break;
            case PluginCanvasAnchor.BottomLeft:
            case PluginCanvasAnchor.BottomCenter:
            case PluginCanvasAnchor.BottomRight:
                y += layerHeight - Height;
                break;
        }
        Left = MathF.Floor(x + 0.5f);
        Top = MathF.Floor(y + 0.5f);
    }

    protected override void OnDraw(UiRenderContext ctx)
    {
        if (_released) return;
        int? frameSlot = _surface.CurrentFrameSlot;
        if (frameSlot is { } slot)
        {
            RepaintIfInvalidated(slot);
            if (_shown is not null)
                _shown.LastUsedFrameSlot = slot;
        }

        if (_shown is not { HasContent: true } shown) return;
        ctx.DrawSprite(shown.Handle, 0f, 0f, Width, Height, 0f, 0f, 1f, 1f, Vector4.One);
    }

    private void RepaintIfInvalidated(int frameSlot)
    {
        if (_guard.IsTripped || _registration.Paint is null) return;
        if (!_registration.TakeInvalidation()) return;

        CanvasTarget? target = AcquireWritable(frameSlot);
        if (target is null)
        {
            // No target can be written this frame; keep the request for the next.
            _registration.Invalidate();
            return;
        }

        bool drew = _surface.Repaint(_registration, target.Target, _guard, _images());
        target.LastUsedFrameSlot = frameSlot;
        if (_guard.IsTripped)
        {
            // The callback was dropped: what it left in the target is not to
            // be trusted, and the registration says why the canvas is gone.
            target.HasContent = false;
            _shown = null;
            _registration.IsDropped = true;
            Visible = false;
            return;
        }
        target.HasContent = drew;
        _shown = target;
    }

    private CanvasTarget? AcquireWritable(int frameSlot)
    {
        foreach (CanvasTarget candidate in _targets)
        {
            if (ReferenceEquals(candidate, _shown)) continue;
            if (candidate.LastUsedFrameSlot == -1 || candidate.LastUsedFrameSlot == frameSlot)
                return candidate;
        }
        if (_targets.Count >= MaximumTargets || _targetsUnavailable)
            return null;

        IGpuDevice device = _surface.Services.Device;
        IGpuRenderTarget target;
        try
        {
            target = device.CreateRenderTarget(new GpuRenderTargetDescription(
                $"plugin-canvas-{_registration.Owner.Id}-{_registration.CanvasId}-{_targets.Count}",
                _registration.Width,
                _registration.Height,
                GpuTextureFormat.Rgba8UnormRenderTarget,
                DepthFormat: null,
                SampleCount: 1));
        }
        catch (Exception failure)
        {
            _targetsUnavailable = true;
            _report(
                $"Plugin canvas '{_registration.Owner.Id}/{_registration.CanvasId}' cannot be painted: "
                + $"no off-screen target ({_registration.Width}x{_registration.Height}): {failure.Message}.");
            return null;
        }

        try
        {
            IGpuSampler sampler = device.CreateSampler(GpuSamplerDescription.WorldClamp);
            GpuTextureSlot slot = device.RegisterTexture(target.ColorTexture, sampler);
            var created = new CanvasTarget(target, slot);
            _targets.Add(created);
            return created;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Gives every target back through the retirement queue, because the
    /// frame in flight may still be sampling the shown one, and stops
    /// drawing. Idempotent. The element is removed from the tree by whoever
    /// mounted it, through the parent, so the root sees the subtree go.
    /// </summary>
    internal void ReleaseTargets()
    {
        if (_released) return;
        _released = true;
        _shown = null;
        IGpuDevice device = _surface.Services.Device;
        foreach (CanvasTarget target in _targets)
        {
            _surface.Services.Retirement.Retire(() =>
            {
                device.ReleaseTextureSlot(target.Slot);
                target.Target.Dispose();
            });
        }
        _targets.Clear();
    }
}
