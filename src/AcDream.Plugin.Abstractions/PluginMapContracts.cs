namespace AcDream.Plugin.Abstractions;

/// <summary>A point in the canonical OpenAC map coordinate system.</summary>
/// <remarks>EastWest and NorthSouth are navigation units (240 metres), with north and east positive.</remarks>
/// <param name="EastWest">How far east of the map's origin, in navigation units; west is negative.</param>
/// <param name="NorthSouth">How far north of the map's origin, in navigation units; south is negative.</param>
/// <param name="Elevation">Height, for a point that has one. A flat map ignores it.</param>
public readonly record struct PluginMapPoint(double EastWest, double NorthSouth, double Elevation = 0);

/// <summary>A rectangular map viewport in canonical world units.</summary>
public readonly record struct PluginMapViewport
{
    /// <summary>Makes a viewport around a centre point.</summary>
    /// <param name="center">The point in the middle of the viewport.</param>
    /// <param name="width">The east-to-west extent, in navigation units. Must be positive.</param>
    /// <param name="height">The north-to-south extent, in navigation units. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">The width or the height is zero or negative.</exception>
    public PluginMapViewport(PluginMapPoint center, double width, double height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Map dimensions must be positive.");
        Center = center;
        Width = width;
        Height = height;
    }

    /// <summary>The point in the middle of the viewport.</summary>
    public PluginMapPoint Center { get; }

    /// <summary>The east-to-west extent, in navigation units.</summary>
    public double Width { get; }

    /// <summary>The north-to-south extent, in navigation units.</summary>
    public double Height { get; }
}

/// <summary>Pixel coordinates in a map image, using a top-left origin.</summary>
/// <param name="X">Pixels from the image's left edge.</param>
/// <param name="Y">Pixels down from the image's top edge.</param>
public readonly record struct PluginMapPixel(double X, double Y);

/// <summary>Converts canonical world coordinates to and from a map image.</summary>
public interface IPluginMapCoordinateConverter
{
    /// <summary>Where a world point falls on an image of the given size.</summary>
    /// <param name="world">The world point.</param>
    /// <param name="imageWidth">The image's width in pixels.</param>
    /// <param name="imageHeight">The image's height in pixels.</param>
    /// <returns>The pixel position; outside the image when the point is outside <see cref="WorldBounds"/>.</returns>
    PluginMapPixel WorldToPixel(PluginMapPoint world, double imageWidth, double imageHeight);

    /// <summary>Which world point a pixel of an image of the given size shows.</summary>
    /// <param name="pixel">The pixel position.</param>
    /// <param name="imageWidth">The image's width in pixels.</param>
    /// <param name="imageHeight">The image's height in pixels.</param>
    /// <returns>The world point. It carries no elevation.</returns>
    PluginMapPoint PixelToWorld(PluginMapPixel pixel, double imageWidth, double imageHeight);

    /// <summary>The part of the world the whole image covers.</summary>
    PluginMapViewport WorldBounds { get; }
}

/// <summary>A linear converter for a map image whose bounds are known in world units.</summary>
/// <remarks>North is up: the image's top edge is the northern edge of the bounds, its left edge the western one.</remarks>
public sealed class LinearPluginMapCoordinateConverter : IPluginMapCoordinateConverter
{
    /// <summary>Makes a converter for an image that covers the given part of the world.</summary>
    /// <param name="worldBounds">The part of the world the whole image covers.</param>
    public LinearPluginMapCoordinateConverter(PluginMapViewport worldBounds) => WorldBounds = worldBounds;

    /// <inheritdoc />
    public PluginMapViewport WorldBounds { get; }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The image width or height is zero or negative.</exception>
    public PluginMapPixel WorldToPixel(PluginMapPoint world, double imageWidth, double imageHeight)
    {
        ValidateImage(imageWidth, imageHeight);
        return new(
            (world.EastWest - (WorldBounds.Center.EastWest - WorldBounds.Width / 2)) / WorldBounds.Width * imageWidth,
            (WorldBounds.Center.NorthSouth + WorldBounds.Height / 2 - world.NorthSouth) / WorldBounds.Height * imageHeight);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentOutOfRangeException">The image width or height is zero or negative.</exception>
    public PluginMapPoint PixelToWorld(PluginMapPixel pixel, double imageWidth, double imageHeight)
    {
        ValidateImage(imageWidth, imageHeight);
        return new(
            WorldBounds.Center.EastWest - WorldBounds.Width / 2 + pixel.X / imageWidth * WorldBounds.Width,
            WorldBounds.Center.NorthSouth + WorldBounds.Height / 2 - pixel.Y / imageHeight * WorldBounds.Height);
    }

    private static void ValidateImage(double width, double height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
    }
}

/// <summary>Describes a map image and its canonical coordinate bounds.</summary>
/// <param name="Id">The id of the image among the plugin's resources.</param>
/// <param name="PixelWidth">The image's width in pixels.</param>
/// <param name="PixelHeight">The image's height in pixels.</param>
/// <param name="Coordinates">How positions on the image relate to positions in the world.</param>
public sealed record PluginMapImage(string Id, int PixelWidth, int PixelHeight, IPluginMapCoordinateConverter Coordinates);

/// <summary>A map marker owned by a plugin.</summary>
/// <param name="Id">The marker's name, unique on its map. Input about the marker names it by this.</param>
/// <param name="Position">Where in the world the marker stands.</param>
/// <param name="Label">Text shown beside the marker, or null for none.</param>
/// <param name="IconId">The id of an icon among the plugin's resources, or null for the host's default marker.</param>
/// <param name="Tooltip">Text shown when the pointer rests on the marker, or null for none.</param>
/// <param name="IsSelected">Whether the marker is drawn as the selected one.</param>
public sealed record PluginMapMarker(string Id, PluginMapPoint Position, string? Label = null, string? IconId = null, string? Tooltip = null, bool IsSelected = false);

/// <summary>Input delivered by a map surface.</summary>
/// <param name="Kind">What the pointer did.</param>
/// <param name="Position">Where on the map's image the pointer was.</param>
/// <param name="WheelDelta">How far the wheel turned, for a wheel event; zero otherwise.</param>
/// <param name="MarkerId">The marker under the pointer, or null when there was none.</param>
public readonly record struct PluginMapInput(PluginMapInputKind Kind, PluginMapPixel Position, double WheelDelta = 0, string? MarkerId = null);

/// <summary>What a pointer did over a map.</summary>
public enum PluginMapInputKind
{
    /// <summary>A button went down.</summary>
    PointerDown,

    /// <summary>A button was let go.</summary>
    PointerUp,

    /// <summary>A button went down and was let go in the same place.</summary>
    Click,

    /// <summary>The wheel turned.</summary>
    Wheel,

    /// <summary>The pointer moved with a button held.</summary>
    Drag,
}

/// <summary>Host-owned map surface suitable for declarative map controls.</summary>
/// <remarks>Dispose it to remove the map and its callbacks.</remarks>
public interface IPluginMapSurface : IDisposable
{
    /// <summary>The part of the world the map shows. Set it to pan or zoom.</summary>
    PluginMapViewport Viewport { get; set; }

    /// <summary>The image drawn under the markers, or null before one is set.</summary>
    PluginMapImage? Background { get; }

    /// <summary>The markers last given to <see cref="SetMarkers"/>.</summary>
    IReadOnlyList<PluginMapMarker> Markers { get; }

    /// <summary>The route last given to <see cref="SetRoute"/>.</summary>
    IReadOnlyList<PluginMapPoint> Route { get; }

    /// <summary>Raised for each pointer event over the map.</summary>
    event Action<PluginMapInput>? Input;

    /// <summary>Sets the image drawn under the markers.</summary>
    /// <param name="image">The image and its world bounds.</param>
    void SetBackground(PluginMapImage image);

    /// <summary>Replaces every marker on the map.</summary>
    /// <param name="markers">The new markers; an empty list clears them.</param>
    void SetMarkers(IReadOnlyList<PluginMapMarker> markers);

    /// <summary>Replaces the route drawn on the map.</summary>
    /// <param name="points">The route's points in walking order; an empty list clears it.</param>
    void SetRoute(IReadOnlyList<PluginMapPoint> points);
}

/// <summary>Map controls exposed by a UI host; inert in headless hosts.</summary>
public interface IPluginMapRegistry
{
    /// <summary>Adds a map. Dispose the returned surface to remove it.</summary>
    /// <param name="mapId">The map's name, unique within the plugin that adds it.</param>
    /// <param name="initialViewport">The part of the world the map shows to begin with.</param>
    /// <returns>The surface the plugin sets the map's image, markers and route through.</returns>
    IPluginMapSurface AddMap(string mapId, PluginMapViewport initialViewport);
}

/// <summary>The map registry of a host that shows no maps.</summary>
/// <remarks>Accepts every call and shows nothing; what is set is not kept. No client shows maps yet, so every host answers with this.</remarks>
public sealed class NoOpPluginMapRegistry : IPluginMapRegistry
{
    /// <summary>The one shared instance.</summary>
    public static NoOpPluginMapRegistry Instance { get; } = new();
    private NoOpPluginMapRegistry() { }

    /// <inheritdoc />
    public IPluginMapSurface AddMap(string mapId, PluginMapViewport initialViewport) => new NoOpMap(initialViewport);
    private sealed class NoOpMap(PluginMapViewport viewport) : IPluginMapSurface
    {
        public PluginMapViewport Viewport { get; set; } = viewport;
        public PluginMapImage? Background => null;
        public IReadOnlyList<PluginMapMarker> Markers => Array.Empty<PluginMapMarker>();
        public IReadOnlyList<PluginMapPoint> Route => Array.Empty<PluginMapPoint>();
        public event Action<PluginMapInput>? Input { add { } remove { } }
        public void SetBackground(PluginMapImage image) { }
        public void SetMarkers(IReadOnlyList<PluginMapMarker> markers) { }
        public void SetRoute(IReadOnlyList<PluginMapPoint> points) { }
        public void Dispose() { }
    }
}
