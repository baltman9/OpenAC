namespace AcDream.Plugin.Abstractions;

/// <summary>Identifies one tile in a tiled map image.</summary>
public readonly record struct PluginMapTileKey
{
    /// <summary>Names one tile.</summary>
    /// <param name="zoom">The zoom level, zero or greater.</param>
    /// <param name="x">The tile's column at that zoom level, zero or greater.</param>
    /// <param name="y">The tile's row at that zoom level, zero or greater.</param>
    /// <exception cref="ArgumentOutOfRangeException">Any of the three is negative.</exception>
    public PluginMapTileKey(int zoom, int x, int y)
    {
        if (zoom < 0) throw new ArgumentOutOfRangeException(nameof(zoom));
        if (x < 0 || y < 0) throw new ArgumentOutOfRangeException(nameof(x));
        Zoom = zoom; X = x; Y = y;
    }

    /// <summary>The zoom level the tile belongs to.</summary>
    public int Zoom { get; }

    /// <summary>The tile's column at its zoom level.</summary>
    public int X { get; }

    /// <summary>The tile's row at its zoom level.</summary>
    public int Y { get; }
}

/// <summary>Decoded immutable map tile returned by a host resource provider.</summary>
/// <param name="Key">Which tile this is.</param>
/// <param name="PixelWidth">The tile's width in pixels.</param>
/// <param name="PixelHeight">The tile's height in pixels.</param>
/// <param name="Pixels">The decoded pixels, row by row from the top.</param>
/// <param name="PixelFormat">How <paramref name="Pixels"/> is laid out; "RGBA8" is four bytes a pixel.</param>
public sealed record PluginMapTile(PluginMapTileKey Key, int PixelWidth, int PixelHeight, ReadOnlyMemory<byte> Pixels, string PixelFormat = "RGBA8");

/// <summary>Metadata and asynchronous tile access for a map resource.</summary>
public interface IPluginTiledMapResource : IAsyncDisposable
{
    /// <summary>The resource id the map was opened from.</summary>
    string Id { get; }

    /// <summary>The width and height of a full tile, in pixels.</summary>
    int TileSize { get; }

    /// <summary>The lowest zoom level the map has tiles for.</summary>
    int MinZoom { get; }

    /// <summary>The highest zoom level the map has tiles for.</summary>
    int MaxZoom { get; }

    /// <summary>The part of the world the whole map covers.</summary>
    PluginMapViewport WorldBounds { get; }

    /// <summary>Loads and decodes one tile.</summary>
    /// <param name="key">Which tile.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The tile, or null when the map has no tile there.</returns>
    ValueTask<PluginMapTile?> LoadTileAsync(PluginMapTileKey key, CancellationToken cancellationToken = default);
}

/// <summary>Loads tiled maps from declared plugin resources.</summary>
public interface IPluginMapResourceCatalog
{
    /// <summary>Opens a tiled map among the plugin's resources.</summary>
    /// <param name="resourceId">The id of the map resource.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The map, or null when the host loads no maps or has no such resource. Dispose it when done.</returns>
    ValueTask<IPluginTiledMapResource?> OpenMapAsync(string resourceId, CancellationToken cancellationToken = default);
}

/// <summary>Inert map resource provider for headless hosts.</summary>
public sealed class NoOpPluginMapResourceCatalog : IPluginMapResourceCatalog
{
    /// <summary>The one shared instance.</summary>
    public static NoOpPluginMapResourceCatalog Instance { get; } = new();
    private NoOpPluginMapResourceCatalog() { }

    /// <inheritdoc />
    public ValueTask<IPluginTiledMapResource?> OpenMapAsync(string resourceId, CancellationToken cancellationToken = default) => ValueTask.FromResult<IPluginTiledMapResource?>(null);
}
