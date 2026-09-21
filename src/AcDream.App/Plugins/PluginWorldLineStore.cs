using AcDream.Plugin.Abstractions;

namespace AcDream.App.Plugins;

public sealed class PluginWorldLineStore : IPluginWorldLines
{
    private readonly List<Layer> _layers = [];
    internal IEnumerable<PluginWorldLine> Lines => _layers.SelectMany(layer => layer.Lines);

    public IPluginWorldLineLayer CreateLayer()
    {
        var layer = new Layer(this);
        _layers.Add(layer);
        return layer;
    }

    private sealed class Layer(PluginWorldLineStore owner) : IPluginWorldLineLayer
    {
        public PluginWorldLine[] Lines { get; private set; } = [];
        private bool _disposed;
        public void SetLines(IReadOnlyList<PluginWorldLine> lines)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Lines = lines.Take(16384).ToArray();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Lines = [];
            owner._layers.Remove(this);
        }
    }
}
