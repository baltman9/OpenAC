namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Which corner or edge of the screen a canvas is measured from. The
/// canvas's offset is added from there, so a canvas anchored at the bottom
/// right with an offset of (-10, -10) sits ten pixels in from that corner
/// and stays there when the window is resized.
/// </summary>
public enum PluginCanvasAnchor
{
    /// <summary>The canvas's top-left corner sits at the screen's top-left corner plus the offset.</summary>
    TopLeft,

    /// <summary>The canvas is centred along the top edge, plus the offset.</summary>
    TopCenter,

    /// <summary>The canvas's top-right corner sits at the screen's top-right corner plus the offset.</summary>
    TopRight,

    /// <summary>The canvas is centred along the left edge, plus the offset.</summary>
    CenterLeft,

    /// <summary>The canvas is centred on the screen, plus the offset.</summary>
    Center,

    /// <summary>The canvas is centred along the right edge, plus the offset.</summary>
    CenterRight,

    /// <summary>The canvas's bottom-left corner sits at the screen's bottom-left corner plus the offset.</summary>
    BottomLeft,

    /// <summary>The canvas is centred along the bottom edge, plus the offset.</summary>
    BottomCenter,

    /// <summary>The canvas's bottom-right corner sits at the screen's bottom-right corner plus the offset.</summary>
    BottomRight,
}

/// <summary>A width and a height, in pixels.</summary>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
public readonly record struct PluginSize(double Width, double Height);

/// <summary>
/// How a plugin's canvas is identified, how large it is and where it sits.
/// A canvas is exactly its declared size: everything painted is clipped to
/// that rectangle, and there is no way to draw anywhere else on the screen.
/// </summary>
/// <param name="CanvasId">The plugin's own short name for this canvas, unique within the plugin.</param>
/// <param name="Width">The canvas's width in pixels, at least 1.</param>
/// <param name="Height">The canvas's height in pixels, at least 1.</param>
public sealed record PluginCanvasDescriptor(string CanvasId, int Width, int Height)
{
    /// <summary>Which corner or edge of the screen the canvas is measured from.</summary>
    public PluginCanvasAnchor Anchor { get; init; } = PluginCanvasAnchor.TopLeft;

    /// <summary>How far from the anchor the canvas sits, in pixels; negative values move it back towards the screen's middle from a right or bottom anchor.</summary>
    public PluginPoint Offset { get; init; }

    /// <summary>Whether the canvas is shown as soon as it is registered.</summary>
    public bool StartVisible { get; init; } = true;
}

/// <summary>
/// What a canvas's paint callback draws with. Coordinates are pixels from
/// the canvas's own top-left corner, y down; everything is clipped to the
/// canvas. The painter is valid only for the duration of the paint
/// callback: keeping it and drawing later throws.
///
/// <para>Text is drawn in the client's own interface font, at its one
/// size, which is why there is no font or size to choose. Every clip
/// pushed during a paint must be popped before it returns.</para>
/// </summary>
public interface IPluginPainter
{
    /// <summary>The canvas's width in pixels.</summary>
    int Width { get; }

    /// <summary>The canvas's height in pixels.</summary>
    int Height { get; }

    /// <summary>Fills the whole canvas with one colour; transparent black clears it.</summary>
    /// <param name="color">The colour to fill with.</param>
    void Clear(PluginColor color);

    /// <summary>Fills a rectangle.</summary>
    /// <param name="rect">The rectangle to fill.</param>
    /// <param name="color">The fill colour.</param>
    void FillRect(PluginRect rect, PluginColor color);

    /// <summary>Draws a rectangle's outline, inside its edges.</summary>
    /// <param name="rect">The rectangle to outline.</param>
    /// <param name="color">The outline colour.</param>
    /// <param name="thickness">The outline's width in pixels.</param>
    void StrokeRect(PluginRect rect, PluginColor color, float thickness = 1f);

    /// <summary>Draws a straight line, centred on the segment, with square ends.</summary>
    /// <param name="from">Where the line starts.</param>
    /// <param name="to">Where the line ends.</param>
    /// <param name="color">The line's colour.</param>
    /// <param name="thickness">The line's width in pixels.</param>
    void DrawLine(PluginPoint from, PluginPoint to, PluginColor color, float thickness = 1f);

    /// <summary>Draws one line of text with its top-left corner at a position.</summary>
    /// <param name="text">The text to draw.</param>
    /// <param name="position">Where the text's top-left corner goes.</param>
    /// <param name="color">The text colour.</param>
    /// <param name="outline">Whether to draw a dark outline around the glyphs, as the client does over the world.</param>
    void DrawText(string text, PluginPoint position, PluginColor color, bool outline = false);

    /// <summary>How much room one line of text takes when drawn.</summary>
    /// <param name="text">The text to measure.</param>
    /// <returns>The text's width and the font's line height, in pixels.</returns>
    PluginSize MeasureText(string text);

    /// <summary>Draws an image stretched over a rectangle.</summary>
    /// <param name="image">An image from <see cref="IPluginImages"/>; a released or invalid image draws nothing.</param>
    /// <param name="destination">The rectangle the image fills.</param>
    /// <param name="tint">A colour the image is multiplied by; <see cref="PluginColor.White"/> leaves it unchanged.</param>
    void DrawImage(PluginImage image, PluginRect destination, PluginColor tint);

    /// <summary>
    /// Draws an image over a rectangle after scaling and turning that
    /// rectangle about a pivot. The pivot is measured in pixels from the
    /// rectangle's own top-left corner, so (0, 0) turns the image about its
    /// top-left and (width / 2, height / 2) about its middle. Rotation is
    /// clockwise on screen.
    /// </summary>
    /// <param name="image">An image from <see cref="IPluginImages"/>; a released or invalid image draws nothing.</param>
    /// <param name="destination">The rectangle the image would fill unturned and unscaled.</param>
    /// <param name="tint">A colour the image is multiplied by; <see cref="PluginColor.White"/> leaves it unchanged.</param>
    /// <param name="rotationRadians">How far to turn the rectangle about the pivot, in radians, clockwise on screen.</param>
    /// <param name="pivot">The point, in pixels from the rectangle's top-left corner, the rectangle turns and scales about.</param>
    /// <param name="scaleX">How much to stretch the rectangle sideways about the pivot; 1 leaves it.</param>
    /// <param name="scaleY">How much to stretch the rectangle vertically about the pivot; 1 leaves it.</param>
    void DrawImageTransformed(
        PluginImage image,
        PluginRect destination,
        PluginColor tint,
        double rotationRadians,
        PluginPoint pivot,
        double scaleX = 1.0,
        double scaleY = 1.0);

    /// <summary>
    /// Restricts what follows to a rectangle, intersected with whatever clip
    /// is already in force. Every push must be matched by a
    /// <see cref="PopClip"/> before the paint callback returns.
    /// </summary>
    /// <param name="rect">The rectangle to clip to.</param>
    void PushClip(PluginRect rect);

    /// <summary>Undoes the most recent <see cref="PushClip"/>.</summary>
    void PopClip();
}

/// <summary>
/// A rectangle the plugin paints, shown over the world and under every
/// window, taking no input. Painting is retained: the host keeps what was
/// last painted and calls the paint callback again only after
/// <see cref="Invalidate"/>, at most once per frame, on the tick thread,
/// with a painter that is valid only for the duration of that call.
///
/// <para>A paint callback that keeps running over its budget on several
/// frames in a row, throws, or leaves a clip pushed is dropped for the
/// rest of the session and the canvas hidden; the client's log says why.
/// Disposing the canvas removes it. On a host that draws nothing the
/// canvas is accepted, <see cref="IsAvailable"/> is false and the paint
/// callback is never called.</para>
/// </summary>
public interface IPluginCanvas : IDisposable
{
    /// <summary>The plugin's own name for this canvas, as registered.</summary>
    string CanvasId { get; }

    /// <summary>The canvas's width in pixels, as registered.</summary>
    int Width { get; }

    /// <summary>The canvas's height in pixels, as registered.</summary>
    int Height { get; }

    /// <summary>Whether the host draws this canvas at all: false without a window.</summary>
    bool IsAvailable => false;

    /// <summary>Whether the canvas is shown. Set it to show or hide the canvas; what was painted is kept while hidden.</summary>
    bool IsVisible { get; set; }

    /// <summary>Which corner or edge of the screen the canvas is measured from. Set it to move the canvas.</summary>
    PluginCanvasAnchor Anchor { get; set; }

    /// <summary>How far from the anchor the canvas sits, in pixels. Set it to move the canvas.</summary>
    PluginPoint Offset { get; set; }

    /// <summary>
    /// Asks for the paint callback to run again, on the next frame at the
    /// earliest. Calling it several times before that frame paints once.
    /// </summary>
    void Invalidate();
}

/// <summary>
/// The canvas a host that draws nothing hands out: it keeps the state the
/// plugin sets, so the plugin's own logic runs unchanged, and never paints.
/// </summary>
public sealed class NoOpPluginCanvas : IPluginCanvas
{
    /// <summary>Makes an inert canvas that answers with the descriptor's values.</summary>
    /// <param name="descriptor">How the canvas was described when registered.</param>
    public NoOpPluginCanvas(PluginCanvasDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        CanvasId = descriptor.CanvasId;
        Width = descriptor.Width;
        Height = descriptor.Height;
        IsVisible = descriptor.StartVisible;
        Anchor = descriptor.Anchor;
        Offset = descriptor.Offset;
    }

    /// <inheritdoc/>
    public string CanvasId { get; }

    /// <inheritdoc/>
    public int Width { get; }

    /// <inheritdoc/>
    public int Height { get; }

    /// <inheritdoc/>
    public bool IsVisible { get; set; }

    /// <inheritdoc/>
    public PluginCanvasAnchor Anchor { get; set; }

    /// <inheritdoc/>
    public PluginPoint Offset { get; set; }

    /// <summary>True once <see cref="Dispose"/> has been called.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Does nothing; there is nothing to paint on.</summary>
    public void Invalidate()
    {
    }

    /// <summary>Marks the canvas disposed; there is nothing to remove.</summary>
    public void Dispose() => IsDisposed = true;
}
