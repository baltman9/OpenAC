using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Entities;

/// <summary>
/// Letting go of an object the client still believes in although the server
/// has stopped talking about it.
/// </summary>
/// <remarks>
/// Which objects may be let go of, and what "it worked" means, are the same
/// questions on every client, so they are answered here: an object that is
/// still active, is not the character itself, and is gone from the directory
/// afterwards. A client that draws has one extra thing to do -- take down
/// what it drew -- so it lends the authoritative-delete route it already runs
/// for a server delete, and a ghost then leaves exactly the way a real
/// deletion does. A client that draws nothing leaves the route alone and the
/// runtime retires the object itself.
/// </remarks>
public sealed class RuntimeGhostDismissal
{
    private readonly RuntimeEntityObjectLifetime _entities;
    private readonly Func<uint> _localPlayerServerGuid;
    private Func<DeleteObject.Parsed, bool>? _authoritativeDelete;

    internal RuntimeGhostDismissal(
        RuntimeEntityObjectLifetime entities,
        Func<uint> localPlayerServerGuid)
    {
        _entities = entities
            ?? throw new ArgumentNullException(nameof(entities));
        _localPlayerServerGuid = localPlayerServerGuid
            ?? throw new ArgumentNullException(nameof(localPlayerServerGuid));
    }

    /// <summary>
    /// Lends this client's own authoritative-delete route, so that dismissing
    /// a ghost takes down everything a server delete would have.
    /// </summary>
    public void BindAuthoritativeDelete(Func<DeleteObject.Parsed, bool> route)
    {
        ArgumentNullException.ThrowIfNull(route);
        _authoritativeDelete = route;
    }

    /// <summary>
    /// Drops the named object. Answers whether the client has stopped
    /// believing in it, which is the only thing a caller can act on.
    /// </summary>
    public bool Dismiss(uint serverGuid)
    {
        if (serverGuid == 0u
            || serverGuid == _localPlayerServerGuid()
            || !_entities.Entities.TryGetActive(
                serverGuid,
                out RuntimeEntityRecord active))
        {
            return false;
        }

        var delete = new DeleteObject.Parsed(serverGuid, active.Incarnation);
        if (_authoritativeDelete is { } route)
            _ = route(delete);
        else
            Retire(delete);

        return !_entities.Entities.TryGetActive(serverGuid, out _);
    }

    /// <summary>
    /// The retirement a client without a drawn world runs: the same accept,
    /// complete and retire an authoritative delete takes.
    /// </summary>
    private void Retire(DeleteObject.Parsed delete)
    {
        if (!_entities.TryAcceptDelete(
                delete,
                isLocalPlayer: false,
                removeRetainedObject: true,
                out RuntimeEntityDeleteAcceptance acceptance))
        {
            return;
        }

        _entities.CompleteAcceptedDelete(acceptance);
        if (acceptance.RetiredCanonical is { } retired)
        {
            Exception? failure = _entities.RetireCanonicalOnly(retired);
            if (failure is not null)
                throw failure;
        }
    }
}
