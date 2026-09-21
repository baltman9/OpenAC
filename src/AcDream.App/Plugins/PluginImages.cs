using AcDream.Plugin.Abstractions;

namespace AcDream.App.Plugins;

/// <summary>
/// One plugin's image surface as the plugin sees it: the contract over its
/// <see cref="PluginImageTable"/>. Disposing it is how the plugin's images
/// go with the plugin -- the scoped registry tracks it like any other
/// registration -- and the registry that made it forgets it so a reloaded
/// plugin starts with a fresh table.
/// </summary>
internal sealed class PluginImages : IPluginImages, IDisposable
{
    private readonly PluginImageTable _table;
    private readonly Action<PluginImages> _forget;
    private bool _disposed;

    internal PluginImages(PluginImageTable table, Action<PluginImages> forget)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _forget = forget ?? throw new ArgumentNullException(nameof(forget));
    }

    internal PluginImageTable Table => _table;

    public bool IsAvailable => !_disposed && _table.IsBound;

    public PluginImage FromClientArt(uint surfaceIdOrIndex) =>
        _disposed
            ? PluginImage.None
            : ToImage(_table.AcquireClientArt(PluginIcons.Normalize(surfaceIdOrIndex)));

    public PluginImage FromSpellIcon(uint spellId) =>
        _disposed ? PluginImage.None : ToImage(_table.AcquireSpellIcon(spellId));

    public PluginImage FromObjectIcon(uint objectId) =>
        _disposed ? PluginImage.None : ToImage(_table.AcquireObjectIcon(objectId));

    public PluginImage FromStream(string name, Func<Stream> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(open);
        return _disposed ? PluginImage.None : ToImage(_table.AcquireDecoded(name, open));
    }

    public bool Release(PluginImage image) =>
        !_disposed && _table.Release(ToHandle(image));

    public int Count => _disposed ? 0 : _table.Count;

    public int MaximumCount => _table.Budget.MaximumCount;

    public long MaximumBytes => _table.Budget.MaximumBytes;

    public int MaximumDimension => _table.Budget.MaximumDimension;

    /// <summary>The interface texture behind an image, for the painter.</summary>
    internal bool TryResolve(PluginImage image, out uint texture, out int width, out int height)
    {
        if (!_disposed)
            return _table.TryResolve(ToHandle(image), out texture, out width, out height);
        texture = 0u;
        width = 0;
        height = 0;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _table.Dispose();
        _forget(this);
    }

    private static PluginImage ToImage(PluginImageHandle handle) =>
        handle.IsValid ? new PluginImage(handle.Id, handle.Width, handle.Height) : PluginImage.None;

    private static PluginImageHandle ToHandle(PluginImage image) =>
        new(image.Handle, image.Width, image.Height);
}
