using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Selection;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// Asks the server to stream the selected creature's health, and to stop when
/// the selection moves on. One owner does this for every host: a host that
/// draws a health meter and a host that draws nothing ask the same questions,
/// so what either can tell a plugin about a creature's health is the same.
/// </summary>
public sealed class RuntimeSelectedObjectHealthQuery : IDisposable
{
    private readonly SelectionState _selection;
    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _playerGuid;
    private readonly Action<uint> _send;
    private uint _streaming;
    private bool _disposed;

    /// <summary>The object whose health the server is currently streaming, or zero.</summary>
    public uint StreamingObjectId => _streaming;

    /// <summary>Binds the query to a selection and the transport that carries it.</summary>
    public RuntimeSelectedObjectHealthQuery(
        SelectionState selection,
        ClientObjectTable objects,
        Func<uint> playerGuid,
        Action<uint> sendQueryHealth)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _playerGuid = playerGuid ?? throw new ArgumentNullException(nameof(playerGuid));
        _send = sendQueryHealth ?? throw new ArgumentNullException(nameof(sendQueryHealth));
        _selection.Changed += OnSelectionChanged;
    }

    /// <summary>Stops answering the selection.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _selection.Changed -= OnSelectionChanged;
    }

    private void OnSelectionChanged(SelectionTransition transition)
    {
        // A stream we opened is closed before another is asked for; the
        // server takes object zero to mean "stop telling me".
        if (_streaming != 0u)
        {
            _streaming = 0u;
            _send(0u);
        }

        if (transition.SelectedObjectId is not uint selected || selected == 0u)
            return;
        uint player = _playerGuid();
        if (!SelectedObjectHealthPolicy.ShouldQueryHealth(
                player,
                _objects.Get(player),
                _objects.Get(selected)))
        {
            return;
        }
        _streaming = selected;
        _send(selected);
    }
}
