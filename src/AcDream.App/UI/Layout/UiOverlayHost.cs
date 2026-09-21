using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.UI.Layout;

/// <summary>
/// Where a full-screen, non-interactive overlay is allowed to sit in the
/// painter's order, and what it must stay clear of.
///
/// <para>Siblings under the interface root are painted lowest z-order first,
/// so a smaller number means further back. A window sits at
/// <see cref="WindowFloor"/> and is only ever raised from there, so anything
/// negative stays behind the chat, the vitals and every other real window no
/// matter how the player stacks them.</para>
///
/// <para>That floor is a convention resting on a default, not a rule anything
/// enforces. Registering a window does not set or check its z-order: a window
/// built in code keeps the element default, which is the floor, and an
/// imported layout root keeps whatever its authored level and read order
/// computed to -- zero and upwards at the level window layouts use, but ten
/// thousand BELOW the floor one level further back, which is under this whole
/// band. A window authored at such a level would be painted over by the
/// overlays, and no z-order arithmetic here can prevent it.</para>
/// </summary>
internal static class UiOverlayZOrder
{
    /// <summary>
    /// Where a real window sits: the element default, which registration
    /// leaves alone and the window manager only ever raises from.
    /// </summary>
    public const int WindowFloor = 0;

    /// <summary>
    /// The highest z-order held by the click-through debug overlays that mount
    /// straight onto the interface root rather than onto the shared host. The
    /// band starts above this so hosted content is drawn over them.
    /// </summary>
    public const int DebugOverlayCeiling = -9_999;

    /// <summary>The back of the band overlays may occupy.</summary>
    public const int BandFloor = -9_000;

    /// <summary>
    /// The front of the band. A thousand below <see cref="WindowFloor"/>, so a
    /// future overlay that needs to sit in front of another one still has room
    /// without anyone being tempted to cross zero.
    /// </summary>
    public const int BandCeiling = -1_000;

    /// <summary>Where the one shared host root sits.</summary>
    public const int SharedHostRoot = BandFloor;
}

/// <summary>
/// A full-screen, non-interactive panel: no background, no border, and it
/// lets every pointer message straight through to whatever is behind it.
///
/// <para>Click-through is a per-element flag with no inheritance, so a
/// transparent parent with an ordinary child still swallows clicks over the
/// child's rectangle. Anything added here is therefore made click-through all
/// the way down, which is the rule this type exists to keep. (It is applied
/// when the subtree is added; an element grafted on later to a plain
/// descendant has to set its own.)</para>
/// </summary>
internal class UiOverlayLayer : UiPanel
{
    public UiOverlayLayer()
    {
        BackgroundColor = Vector4.Zero;
        BorderColor = Vector4.Zero;
        ClickThrough = true;
        // The owner drives the rectangle; an anchor would fight it on resize.
        Anchors = AnchorEdges.None;
    }

    public override void AddChild(UiElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        MakeSubtreeClickThrough(child);
        base.AddChild(child);
    }

    private static void MakeSubtreeClickThrough(UiElement element)
    {
        element.ClickThrough = true;
        for (int index = 0; index < element.Children.Count; index++)
            MakeSubtreeClickThrough(element.Children[index]);
    }
}

/// <summary>
/// The one owner of the click-through overlay band. Everything that wants to
/// paint over the world without taking input asks for a layer here instead of
/// mounting its own panel on the interface root and rediscovering the four
/// rules that make such a panel behave: anchors off, rectangle set to the
/// viewport, click-through on the children as well as the panel, and a
/// z-order above the debug overlays but under every real window.
///
/// <para>Children are clipped to their parent's rectangle by default, so an
/// overlay whose rectangle is still zero-sized shows nothing at all. That is
/// why the host seeds its rectangle from the interface root at mount and
/// re-applies it to the root and every layer on
/// <see cref="SetViewport(Vector2)"/>.</para>
/// </summary>
internal sealed class UiOverlayHost
{
    private readonly UiOverlayLayer _root;
    private readonly List<UiOverlayLayer> _layers = [];
    private Vector2 _viewport;

    private UiOverlayHost(UiOverlayLayer root, Vector2 viewport)
    {
        _root = root;
        _viewport = viewport;
    }

    /// <summary>The band-resident panel every layer hangs from.</summary>
    internal UiPanel Root => _root;

    /// <summary>The rectangle the root and every layer currently cover.</summary>
    internal Vector2 Viewport => _viewport;

    internal static UiOverlayHost Mount(UiRoot host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var root = new UiOverlayLayer
        {
            Name = "UiOverlayHost",
            ZOrder = UiOverlayZOrder.SharedHostRoot,
        };
        host.AddChild(root);
        var overlayHost = new UiOverlayHost(root, new Vector2(host.Width, host.Height));
        overlayHost.ApplyViewport();
        return overlayHost;
    }

    /// <summary>
    /// A layer of its own for one owner, so two owners never fight over one
    /// panel's rectangle or visibility. Layers start hidden and cover the
    /// whole viewport; the owner turns its layer on when it has something to
    /// show.
    /// </summary>
    internal UiOverlayLayer AddLayer(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var layer = new UiOverlayLayer { Name = name, Visible = false };
        _layers.Add(layer);
        _root.AddChild(layer);
        ApplyViewport(layer);
        return layer;
    }

    /// <summary>
    /// Points the host at the rectangle the world is being drawn into. Every
    /// layer covers the same rectangle: an overlay is by definition the whole
    /// screen, and one shared value keeps two owners from clipping each other.
    /// </summary>
    internal void SetViewport(Vector2 viewport)
    {
        if (_viewport == viewport) return;
        _viewport = viewport;
        ApplyViewport();
    }

    private void ApplyViewport()
    {
        ApplyViewport(_root);
        for (int index = 0; index < _layers.Count; index++)
            ApplyViewport(_layers[index]);
    }

    private void ApplyViewport(UiElement element)
    {
        element.Left = 0f;
        element.Top = 0f;
        element.Width = _viewport.X;
        element.Height = _viewport.Y;
    }
}
