namespace AcDream.Plugin.Abstractions;

/// <summary>Thread-safe stateful map surface useful to hosts and test fixtures.</summary>
public sealed class PluginMapSurface : IPluginMapSurface
{
    private readonly object _gate = new();
    private PluginMapImage? _background;
    private IReadOnlyList<PluginMapMarker> _markers = Array.Empty<PluginMapMarker>();
    private IReadOnlyList<PluginMapPoint> _route = Array.Empty<PluginMapPoint>();
    private bool _disposed;

    /// <summary>Makes a surface showing the given part of the world.</summary>
    /// <param name="viewport">The part of the world shown to begin with.</param>
    public PluginMapSurface(PluginMapViewport viewport) => Viewport = viewport;

    /// <inheritdoc />
    public PluginMapViewport Viewport { get; set; }

    /// <inheritdoc />
    public PluginMapImage? Background { get { lock (_gate) return _background; } }

    /// <inheritdoc />
    public IReadOnlyList<PluginMapMarker> Markers { get { lock (_gate) return _markers; } }

    /// <inheritdoc />
    public IReadOnlyList<PluginMapPoint> Route { get { lock (_gate) return _route; } }

    /// <inheritdoc />
    public event Action<PluginMapInput>? Input;

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">The surface has been disposed.</exception>
    public void SetBackground(PluginMapImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_gate) { ThrowIfDisposed(); _background = image; }
    }

    /// <inheritdoc />
    /// <remarks>The list is copied, so later changes to it do not reach the map.</remarks>
    /// <exception cref="ObjectDisposedException">The surface has been disposed.</exception>
    public void SetMarkers(IReadOnlyList<PluginMapMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        lock (_gate) { ThrowIfDisposed(); _markers = markers.ToArray(); }
    }

    /// <inheritdoc />
    /// <remarks>The list is copied, so later changes to it do not reach the map.</remarks>
    /// <exception cref="ObjectDisposedException">The surface has been disposed.</exception>
    public void SetRoute(IReadOnlyList<PluginMapPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        lock (_gate) { ThrowIfDisposed(); _route = points.ToArray(); }
    }

    /// <summary>Delivers input to subscribers, if the surface is still active.</summary>
    /// <param name="input">The pointer event to deliver.</param>
    /// <exception cref="ObjectDisposedException">The surface has been disposed.</exception>
    public void RaiseInput(PluginMapInput input)
    {
        Action<PluginMapInput>? handler;
        lock (_gate) { ThrowIfDisposed(); handler = Input; }
        handler?.Invoke(input);
    }

    /// <summary>Drops the background, markers, route and every subscriber. Setting anything afterwards throws.</summary>
    public void Dispose()
    {
        lock (_gate) { _disposed = true; Input = null; _background = null; _markers = Array.Empty<PluginMapMarker>(); _route = Array.Empty<PluginMapPoint>(); }
    }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(PluginMapSurface)); }
}
