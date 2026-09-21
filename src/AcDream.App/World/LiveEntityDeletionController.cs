using AcDream.App.Input;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Entities;

namespace AcDream.App.World;

/// <summary>
/// Owns authoritative and expiry-driven live-object deletion through one
/// generation-safe runtime transaction. As the graphical host's expiry
/// sink it tears the projection down on the way out.
/// </summary>
internal sealed class LiveEntityDeletionController : IRuntimeEntityExpirySink
{
    private readonly LiveEntityRuntime _runtime;
    private readonly ILiveEntityTeardownCoordinator _teardown;
    private readonly ILocalPlayerIdentitySource _identity;

    public LiveEntityDeletionController(
        LiveEntityRuntime runtime,
        RuntimeEntityObjectLifetime entityObjects,
        ILiveEntityTeardownCoordinator teardown,
        ILocalPlayerIdentitySource identity)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        ArgumentNullException.ThrowIfNull(entityObjects);
        _teardown = teardown ?? throw new ArgumentNullException(nameof(teardown));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
    }

    public bool Delete(DeleteObject.Parsed delete)
    {
        if (delete.Guid == _identity.ServerGuid)
            return false;

        bool hasActiveRecord = _runtime.TryGetRecord(delete.Guid, out _);
        if (!hasActiveRecord)
            _teardown.ForgetUnknownOwner(delete.Guid);

        return _runtime.UnregisterLiveEntity(
            delete,
            isLocalPlayer: false,
            removeRetainedObject: true);
    }

    /// <summary>
    /// Destroys an object whose 25-second out-of-visibility deadline expired.
    /// This is a full delete, the same as a server delete: the server forgets
    /// the object on the same schedule and re-sends a create when the player
    /// returns, so nothing is kept to rebuild it from.
    /// </summary>
    public bool Expire(RuntimeEntityExpiryCandidate candidate)
    {
        if (!_runtime.TryGetRecord(
                candidate.Key,
                out LiveEntityRecord record)
            || record.ServerGuid != candidate.ServerGuid)
        {
            return false;
        }

        return _runtime.UnregisterLiveEntity(
            new DeleteObject.Parsed(
                candidate.ServerGuid,
                candidate.Generation),
            isLocalPlayer: false,
            removeRetainedObject: true);
    }
}
