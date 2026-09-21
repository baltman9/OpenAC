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
///
/// <para>A canvas that opted in to pointer input answers the hit-test for
/// its own rectangle while it has a handler, and for nothing outside it.
/// The root gives it the pointer on a press, as it does any element that
/// owns its interior drag, so the moves and the release reach it wherever
/// the pointer goes; the plugin sees every event in the canvas's own
/// pixels. The handler runs under a guard of its own: one that throws or
/// stays slow is dropped, and the canvas goes back to click-through while
/// its painting carries on.</para>
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
    private readonly UiDrawCallbackGuard _inputGuard;
    private readonly Func<PluginKeyModifiers> _modifiers;
    private readonly Action<string> _report;
    private readonly List<CanvasTarget> _targets = [];
    private CanvasTarget? _shown;
    private bool _targetsUnavailable;
    private bool _released;
    private PluginPointerButton _heldButton;
    private readonly Action? _pointerRelease;

    internal PluginCanvasElement(
        PluginCanvasRegistration registration,
        PluginCanvasSurface surface,
        Func<PluginImages?> images,
        Action<string>? report = null,
        Func<double>? nowMilliseconds = null,
        Func<PluginKeyModifiers>? modifiers = null)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _report = report ?? (line => Serilog.Log.Warning("{Line}", line));
        _modifiers = modifiers ?? (static () => PluginKeyModifiers.None);
        _guard = new UiDrawCallbackGuard(
            $"plugin canvas {registration.Owner.Id}/{registration.CanvasId}",
            _report,
            nowMilliseconds,
            RepaintBudgetMilliseconds);
        _inputGuard = new UiDrawCallbackGuard(
            $"plugin canvas {registration.Owner.Id}/{registration.CanvasId} pointer handler",
            _report,
            nowMilliseconds,
            UiDrawCallbackGuard.FrameBudgetMilliseconds,
            callUnit: "events");
        Name = $"PluginCanvas:{registration.Owner.Id}:{registration.CanvasId}";
        // Click-through is the element's own state; the layer it is added
        // to leaves it alone for a canvas that takes input.
        ClickThrough = !registration.AcceptsPointerInput;
        // No drag-and-drop from a press on the canvas: the press is the
        // plugin's, held until the button comes up, wherever the pointer went.
        CapturesPointerDrag = registration.AcceptsPointerInput;
        Anchors = AnchorEdges.None;
        Width = registration.Width;
        Height = registration.Height;
        // Read by the tree before the visibility check, so a canvas the
        // plugin hid and shows again is ticked and drawn on the next frame.
        VisibleSource = () => _registration.IsVisible && !_registration.IsDropped && !_released;
        Visible = VisibleSource();
        if (registration.AcceptsPointerInput)
        {
            _pointerRelease = ReleasePointer;
            registration.PointerRelease = _pointerRelease;
        }
    }

    internal PluginCanvasRegistration Registration => _registration;

    internal UiDrawCallbackGuard Guard => _guard;

    internal int TargetCount => _targets.Count;

    /// <summary>The interface texture currently shown, or 0 before the first paint.</summary>
    internal uint ShownTextureHandle => _shown is { HasContent: true } shown ? shown.Handle : 0u;

    /// <summary>True once the pointer handler was dropped; the canvas is click-through from then on.</summary>
    internal bool IsInputDropped => _inputGuard.IsTripped;

    /// <summary>
    /// Whether the canvas answers for its rectangle right now: it opted in,
    /// someone is listening, and the listener has not been dropped.
    /// </summary>
    private bool TakesInput =>
        _registration.AcceptsPointerInput && !_inputGuard.IsTripped && _registration.PointerHandler is not null;

    protected override bool ClipsChildren => true;

    protected override void OnTick(double deltaSeconds) => Layout();

    protected override bool OnHitTest(float localX, float localY) =>
        TakesInput && base.OnHitTest(localX, localY);

    public override bool OnEvent(in UiEvent e)
    {
        if (!ReferenceEquals(e.Target, this))
            return false;

        switch (e.Type)
        {
            case UiEventType.MouseDown:
                return Press(PluginPointerButton.Left, e.Data1, e.Data2);
            case UiEventType.RightDown:
                return Press(PluginPointerButton.Right, e.Data1, e.Data2);
            case UiEventType.MiddleDown:
                return Press(PluginPointerButton.Middle, e.Data1, e.Data2);

            case UiEventType.MouseMove:
                if (_heldButton == PluginPointerButton.None) return false;
                Deliver(PluginPointerEventKind.Move, e.Data1, e.Data2, _heldButton);
                return true;

            case UiEventType.MouseUp:
                return Release(PluginPointerButton.Left, e.Data1, e.Data2);
            case UiEventType.RightUp:
                return Release(PluginPointerButton.Right, e.Data1, e.Data2);
            case UiEventType.MiddleUp:
                return Release(PluginPointerButton.Middle, e.Data1, e.Data2);

            case UiEventType.Scroll:
            {
                // The root's scroll coordinates belong to the top-level child
                // it hit, not to this element; the pointer's own position does.
                if (FindRoot() is not { } root || !TakesInput) return false;
                Vector2 screen = ScreenPosition;
                Deliver(
                    PluginPointerEventKind.Wheel,
                    root.MouseX - (int)screen.X,
                    root.MouseY - (int)screen.Y,
                    PluginPointerButton.None,
                    e.Data0);
                return true;
            }

            case UiEventType.CaptureChanged:
            {
                // The root took the pointer back with a button still held:
                // the canvas was hidden or removed, or something else
                // captured. The plugin's release clears the button first,
                // so it never sees a cancel it asked for.
                if (_heldButton == PluginPointerButton.None) return false;
                PluginPointerButton held = _heldButton;
                _heldButton = PluginPointerButton.None;
                if (FindRoot() is { } root)
                {
                    Vector2 screen = ScreenPosition;
                    Deliver(PluginPointerEventKind.Cancelled, root.MouseX - (int)screen.X, root.MouseY - (int)screen.Y, held);
                }
                return true;
            }

            // Click, double-click and right-click are the root's reading of a
            // press and release the plugin already saw; nothing beneath the
            // canvas should act on them either.
            case UiEventType.Click:
            case UiEventType.DoubleClick:
            case UiEventType.RightClick:
                return true;
        }
        return false;
    }

    private bool Press(PluginPointerButton button, int localX, int localY)
    {
        if (!TakesInput) return false;
        if (_heldButton == PluginPointerButton.None)
            _heldButton = button;
        Deliver(PluginPointerEventKind.Down, localX, localY, button);
        return true;
    }

    private bool Release(PluginPointerButton button, int localX, int localY)
    {
        if (_heldButton != button) return false;
        _heldButton = PluginPointerButton.None;
        Deliver(PluginPointerEventKind.Up, localX, localY, button);
        return true;
    }

    /// <summary>
    /// Ends the press the canvas holds, on the plugin's request. The button
    /// is forgotten before the root is asked, so the capture change that
    /// follows is not reported back as a cancel.
    /// </summary>
    internal void ReleasePointer()
    {
        if (_heldButton == PluginPointerButton.None) return;
        _heldButton = PluginPointerButton.None;
        if (FindRoot() is { } root && ReferenceEquals(root.Captured, this))
            root.ReleaseCapture();
    }

    private void Deliver(PluginPointerEventKind kind, int localX, int localY, PluginPointerButton button, int wheelDelta = 0)
    {
        Action<PluginPointerEvent>? handler = _registration.PointerHandler;
        if (handler is null || _inputGuard.IsTripped) return;
        var pointer = new PluginPointerEvent(kind, new PluginPoint(localX, localY), button, _modifiers(), wheelDelta);
        _inputGuard.Invoke(() => handler(pointer));
        if (_inputGuard.IsTripped)
        {
            // Nobody is listening any more: let go of the pointer so the
            // press does not hang, and stop answering the hit-test.
            ReleasePointer();
        }
    }

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
        if (ReferenceEquals(_registration.PointerRelease, _pointerRelease))
            _registration.PointerRelease = null;
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
