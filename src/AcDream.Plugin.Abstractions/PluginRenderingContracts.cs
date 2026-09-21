namespace AcDream.Plugin.Abstractions;

/// <summary>Stable metadata used to create a plugin-owned overlay.</summary>
/// <remarks>
/// No client draws overlays yet: every host answers with
/// <see cref="NoOpPluginRenderRegistry"/>. The unit and origin of every
/// position and size in this file are fixed by the first client that does.
/// </remarks>
/// <param name="Id">The overlay's name, unique within the plugin that adds it.</param>
/// <param name="Title">The name shown to the player for this overlay.</param>
/// <param name="DefaultBounds">Where the overlay sits, and how large it is, until the player or the plugin moves it.</param>
/// <param name="Visible">Whether the overlay starts out shown.</param>
/// <param name="Movable">Whether the player may drag the overlay.</param>
/// <param name="Resizable">Whether the player may resize the overlay.</param>
/// <param name="ClickThrough">When true, pointer input passes through the overlay to whatever is under it, and the overlay receives none.</param>
/// <param name="Layer">Drawing order among overlays: a higher layer is drawn over a lower one.</param>
public sealed record PluginHudDescriptor(
    string Id,
    string Title,
    PluginHudBounds DefaultBounds,
    bool Visible = true,
    bool Movable = true,
    bool Resizable = true,
    bool ClickThrough = false,
    int Layer = 0);

/// <summary>The rectangle an overlay occupies.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct PluginHudBounds(double X, double Y, double Width, double Height);

/// <summary>A position on a render surface.</summary>
/// <param name="X">The horizontal position.</param>
/// <param name="Y">The vertical position.</param>
public readonly record struct PluginPoint(double X, double Y);

/// <summary>A rectangle on a render surface.</summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct PluginRect(double X, double Y, double Width, double Height);

/// <summary>A colour with an opacity, one byte per channel.</summary>
/// <param name="R">Red, 0 to 255.</param>
/// <param name="G">Green, 0 to 255.</param>
/// <param name="B">Blue, 0 to 255.</param>
/// <param name="A">Opacity, 0 (transparent) to 255 (opaque).</param>
public readonly record struct PluginColor(byte R, byte G, byte B, byte A = 255);

/// <summary>How a piece of text is drawn.</summary>
/// <param name="FontFamily">The name of the font family asked for.</param>
/// <param name="Size">The text size.</param>
/// <param name="Color">The text colour.</param>
/// <param name="Bold">Whether the text is drawn bold.</param>
public readonly record struct PluginTextStyle(string FontFamily, float Size, PluginColor Color, bool Bold = false);

/// <summary>One pointer event delivered to an overlay.</summary>
/// <param name="Kind">What the pointer did.</param>
/// <param name="Position">Where the pointer was when it happened.</param>
/// <param name="Button">The mouse button involved, or <see cref="PluginMouseButton.None"/> for a move.</param>
/// <param name="Shift">Whether Shift was held.</param>
/// <param name="Control">Whether Control was held.</param>
/// <param name="Alt">Whether Alt was held.</param>
public readonly record struct PluginHudInput(PluginHudInputKind Kind, PluginPoint Position, PluginMouseButton Button = PluginMouseButton.None, bool Shift = false, bool Control = false, bool Alt = false);

/// <summary>What a pointer did over an overlay.</summary>
public enum PluginHudInputKind
{
    /// <summary>A button went down.</summary>
    PointerDown,

    /// <summary>A button was let go.</summary>
    PointerUp,

    /// <summary>A button went down and was let go in the same place.</summary>
    Click,

    /// <summary>The pointer moved.</summary>
    PointerMove,
}

/// <summary>Which mouse button an overlay event is about.</summary>
public enum PluginMouseButton
{
    /// <summary>No button: the event is a move.</summary>
    None,

    /// <summary>The left button.</summary>
    Left,

    /// <summary>The middle button.</summary>
    Middle,

    /// <summary>The right button.</summary>
    Right,
}

/// <summary>Immutable host-managed texture metadata.</summary>
/// <remarks>The host owns the pixels. Disposing the texture tells the host the plugin is done with it.</remarks>
public interface IPluginTexture : IDisposable
{
    /// <summary>The resource id the texture was loaded from.</summary>
    string ResourceId { get; }

    /// <summary>The texture's width in pixels.</summary>
    int Width { get; }

    /// <summary>The texture's height in pixels.</summary>
    int Height { get; }
}

/// <summary>Commands emitted during one host-owned HUD frame.</summary>
public interface IPluginRenderSurface
{
    /// <summary>Starts a frame. Whatever was drawn in the frame before is discarded.</summary>
    void BeginFrame();

    /// <summary>Draws a texture stretched over a rectangle.</summary>
    /// <param name="texture">A texture from <see cref="IPluginRenderRegistry.LoadTexture"/>.</param>
    /// <param name="destination">The rectangle the texture fills.</param>
    /// <param name="color">A colour the texture is multiplied by; opaque white leaves it unchanged.</param>
    void DrawTexture(IPluginTexture texture, PluginRect destination, PluginColor color);

    /// <summary>Draws a straight line.</summary>
    /// <param name="from">Where the line starts.</param>
    /// <param name="to">Where the line ends.</param>
    /// <param name="color">The line's colour.</param>
    /// <param name="thickness">The line's thickness.</param>
    void DrawLine(PluginPoint from, PluginPoint to, PluginColor color, float thickness);

    /// <summary>Draws one run of text.</summary>
    /// <param name="text">The text to draw.</param>
    /// <param name="position">Where the text starts.</param>
    /// <param name="style">The font, size and colour.</param>
    void DrawText(string text, PluginPoint position, PluginTextStyle style);

    /// <summary>Ends the frame and hands what was drawn to the host to show.</summary>
    void EndFrame();
}

/// <summary>A plugin-owned transparent overlay. Disposal unregisters all callbacks.</summary>
public interface IPluginHudRegistration : IDisposable
{
    /// <summary>Whether the overlay is shown. Set it to show or hide the overlay.</summary>
    bool IsVisible { get; set; }

    /// <summary>Where the overlay is and how large. Set it to move or resize the overlay.</summary>
    PluginHudBounds Bounds { get; set; }

    /// <summary>Raised for each pointer event over the overlay. Never raised for a click-through overlay.</summary>
    event Action<PluginHudInput>? Input;

    /// <summary>The surface the plugin draws this overlay's frames on.</summary>
    IPluginRenderSurface Surface { get; }
}

/// <summary>Host renderer services. Plugins never receive graphics-device types.</summary>
public interface IPluginRenderRegistry
{
    /// <summary>Adds an overlay. Dispose the returned registration to remove it.</summary>
    /// <param name="descriptor">The overlay's name, title and starting placement.</param>
    /// <returns>The handle the plugin shows, moves and draws the overlay through.</returns>
    IPluginHudRegistration AddHud(PluginHudDescriptor descriptor);

    /// <summary>Loads an image for drawing.</summary>
    /// <param name="resourceId">The id of an image among the plugin's resources.</param>
    /// <returns>The texture, or null when the host has no renderer or the resource is not an image it can load.</returns>
    IPluginTexture? LoadTexture(string resourceId);
}

/// <summary>Headless renderer implementation.</summary>
/// <remarks>Accepts every call and draws nothing, so a plugin with an overlay runs unchanged where nothing is drawn.</remarks>
public sealed class NoOpPluginRenderRegistry : IPluginRenderRegistry
{
    /// <summary>The one shared instance.</summary>
    public static NoOpPluginRenderRegistry Instance { get; } = new();
    private NoOpPluginRenderRegistry() { }

    /// <inheritdoc />
    public IPluginHudRegistration AddHud(PluginHudDescriptor descriptor) => new NoOpHud(descriptor.DefaultBounds);

    /// <inheritdoc />
    public IPluginTexture? LoadTexture(string resourceId) => null;
    private sealed class NoOpHud(PluginHudBounds bounds) : IPluginHudRegistration
    {
        public bool IsVisible { get; set; }
        public PluginHudBounds Bounds { get; set; } = bounds;
        public event Action<PluginHudInput>? Input { add { } remove { } }
        public IPluginRenderSurface Surface { get; } = new NoOpSurface();
        public void Dispose() { }
    }
    private sealed class NoOpSurface : IPluginRenderSurface
    {
        public void BeginFrame() { }
        public void DrawTexture(IPluginTexture texture, PluginRect destination, PluginColor color) { }
        public void DrawLine(PluginPoint from, PluginPoint to, PluginColor color, float thickness) { }
        public void DrawText(string text, PluginPoint position, PluginTextStyle style) { }
        public void EndFrame() { }
    }
}
