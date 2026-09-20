using AcDream.Core.Selection;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// Keeps what the player has picked out honest about what is still there.
/// When the object the player has selected leaves the world, or the world
/// stops showing it, the selection lets it go.
///
/// This used to be two lambdas on the drawing side of the client, so a client
/// with no window kept pointing at an object that had gone -- and a plugin
/// reading the selection got a guid nothing would answer to. The signals it
/// watches are the ones the runtime raises for every client, so both answer
/// the same way. It is the same move the death clear made: one owner, next to
/// the selection itself.
/// </summary>
public sealed class RuntimeSelectionEntityFollower
    : IRuntimeEntityObjectObserver, IDisposable
{
    private readonly SelectionState _selection;
    private readonly IDisposable _subscription;
    private bool _disposed;

    /// <summary>
    /// Watches <paramref name="events"/> on behalf of
    /// <paramref name="selection"/>.
    /// </summary>
    public RuntimeSelectionEntityFollower(
        RuntimeEntityObjectEventStream events,
        SelectionState selection)
    {
        ArgumentNullException.ThrowIfNull(events);
        _selection = selection
            ?? throw new ArgumentNullException(nameof(selection));
        _subscription = events.Subscribe(this);
    }

    /// <inheritdoc />
    public void OnEntity(in RuntimeEntityDelta delta)
    {
        SelectionChangeReason reason;
        switch (delta.Change)
        {
            // Taken out of the world, or its place in the world given up:
            // the selection is pointing at nothing. A handover to a fresh
            // incarnation of the same object is not that: the server is
            // re-sending something the player still has picked out, and the
            // replacement is registered under the same id in the same
            // breath. Letting go there would drop the selection every time
            // the server re-describes what the player is looking at.
            case RuntimeEntityChange.Deleted when delta.ReplacedInPlace:
                return;
            case RuntimeEntityChange.Deleted:
            case RuntimeEntityChange.Withdrawn:
                reason = SelectionChangeReason.SelectedObjectRemoved;
                break;
            // Still there, but the world has stopped showing it, which is
            // what the client treats as out of reach.
            case RuntimeEntityChange.Hidden:
                reason = SelectionChangeReason.Cleared;
                break;
            default:
                return;
        }

        uint serverGuid = delta.Entity.Identity.ServerGuid;
        if (serverGuid == 0u || _selection.SelectedObjectId != serverGuid)
            return;
        _selection.Clear(SelectionChangeSource.System, reason);
    }

    /// <inheritdoc />
    public void OnInventory(in RuntimeInventoryDelta delta)
    {
    }

    /// <summary>Stops watching.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subscription.Dispose();
    }
}
