using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.World.Cells;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Entities;

internal readonly record struct RuntimeEntityLivenessSample(
    RuntimeEntityKey Key,
    uint ServerGuid,
    bool IsConservativelyVisible,
    bool HasNonWorldRetention);

/// <summary>An object whose out-of-visibility deadline expired.</summary>
internal readonly record struct RuntimeEntityExpiryCandidate(
    RuntimeEntityKey Key,
    uint ServerGuid,
    ushort Generation)
{
    public RuntimeEntityExpiryCandidate(RuntimeEntityKey key, uint serverGuid)
        : this(key, serverGuid, key.Incarnation)
    {
    }
}

/// <summary>
/// Deadline state for objects that left visibility. The original client
/// puts an object on a destruction queue 25 seconds out when its cell is
/// released and takes it off again when the cell comes back; the server
/// keeps the same 25-second clock on its side and re-sends the object when
/// the player returns.
/// </summary>
internal sealed class RuntimeEntityLivenessTracker
{
    internal const double DestructionTimeoutSeconds = 25.0;

    private readonly Dictionary<RuntimeEntityKey, double> _deadlines = [];
    private readonly HashSet<RuntimeEntityKey> _present = [];
    private readonly List<RuntimeEntityKey> _stale = [];
    private readonly List<RuntimeEntityExpiryCandidate> _due = [];

    internal int DeadlineCount => _deadlines.Count;

    internal IReadOnlyList<RuntimeEntityExpiryCandidate> Tick(
        double now,
        IReadOnlyList<RuntimeEntityLivenessSample> samples)
    {
        _present.Clear();
        _due.Clear();
        for (int i = 0; i < samples.Count; i++)
        {
            RuntimeEntityLivenessSample sample = samples[i];
            _present.Add(sample.Key);
            if (sample.IsConservativelyVisible || sample.HasNonWorldRetention)
            {
                _deadlines.Remove(sample.Key);
                continue;
            }

            if (!_deadlines.TryGetValue(sample.Key, out double expiresAt))
            {
                _deadlines[sample.Key] = now + DestructionTimeoutSeconds;
                continue;
            }

            if (expiresAt > now)
                continue;

            _due.Add(new RuntimeEntityExpiryCandidate(sample.Key, sample.ServerGuid));
            _deadlines.Remove(sample.Key);
        }

        _stale.Clear();
        foreach (RuntimeEntityKey key in _deadlines.Keys)
        {
            if (!_present.Contains(key))
                _stale.Add(key);
        }
        for (int i = 0; i < _stale.Count; i++)
            _deadlines.Remove(_stale[i]);

        return _due;
    }

    internal void Clear() => _deadlines.Clear();
}

/// <summary>
/// The player's current cell as the physics engine tracks it. The original
/// client keeps the same pointer in its cell manager and flushes cells from
/// it on every cell change.
/// </summary>
internal interface IRuntimeEntityCurrentCellSource
{
    ObjCell? CurrentCell { get; }
}

internal sealed class RuntimePhysicsCurrentCellSource
    : IRuntimeEntityCurrentCellSource
{
    private readonly RuntimeEntityObjectLifetime _entities;

    public RuntimePhysicsCurrentCellSource(RuntimeEntityObjectLifetime entities) =>
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));

    public ObjCell? CurrentCell =>
        _entities.Physics.Engine.DataCache?.CellGraph.CurrCell;
}

/// <summary>
/// The cells the client holds loaded around the player, which is the set
/// whose objects stay alive. Inside a sealed dungeon cell (an indoor cell not
/// seen from outside) the original client loads only the player's cell and
/// the cells on its authored visible-cell list; on the next cell change every
/// other cell is flushed and its objects start the 25-second deadline, and
/// walking back into a cell that lists them cancels it. The server forgets
/// an object on the same set and only announces a destroyed object to
/// clients that still know it, so an object kept beyond this set is never
/// deleted by a message. Outdoors, and in indoor cells seen from outside,
/// the loaded set is the landblock neighbourhood.
/// </summary>
internal sealed class RuntimeEntityVisibleCellSet
{
    private readonly HashSet<uint> _sealedCells = [];
    private uint _sealedCellId;
    private uint _playerCellId;

    public bool IsSealedDungeon { get; private set; }

    public void Update(ObjCell? playerCell, uint playerCellId)
    {
        _playerCellId = playerCellId;
        // The physics cell can lag the record by a landblock while the
        // destination is still loading. Its visible list says nothing
        // about the new landblock, so nothing is judged per cell until
        // the two agree.
        if (playerCell is not EnvCell { SeenOutside: false } sealedCell
            || (sealedCell.Id & 0xFFFF0000u) != (playerCellId & 0xFFFF0000u))
        {
            IsSealedDungeon = false;
            _sealedCellId = 0u;
            _sealedCells.Clear();
            return;
        }

        IsSealedDungeon = true;
        if (_sealedCellId == sealedCell.Id)
            return;

        _sealedCellId = sealedCell.Id;
        _sealedCells.Clear();
        _sealedCells.Add(sealedCell.Id);
        IReadOnlyList<uint> visible = sealedCell.StabList;
        for (int i = 0; i < visible.Count; i++)
            _sealedCells.Add(visible[i]);
    }

    public bool Contains(uint entityCell) =>
        IsSealedDungeon
            ? _sealedCells.Contains(entityCell)
            : RuntimeEntityLivenessController.IsWithinVisibleLandblocks(
                _playerCellId,
                entityCell);
}

/// <summary>
/// Destroys an object whose out-of-visibility deadline expired. This is a
/// full delete, the same as a server delete: the server forgets the object
/// on the same schedule and re-sends a create when the player returns, so
/// nothing is kept to rebuild it from. A host with presentation tears its
/// projection down on the way; a host without one deletes the canonical
/// object directly (<see cref="RuntimeCanonicalEntityExpirySink"/>).
/// </summary>
internal interface IRuntimeEntityExpirySink
{
    bool Expire(RuntimeEntityExpiryCandidate candidate);

    /// <summary>
    /// Destroys an object that is not in the world (a container's contents)
    /// when its queued deadline expires, through the same path a server
    /// delete takes.
    /// </summary>
    bool Destroy(DeleteObject.Parsed delete);
}

/// <summary>
/// The expiry sink for a host with no presentation: the same canonical
/// delete a server delete performs there.
/// </summary>
internal sealed class RuntimeCanonicalEntityExpirySink : IRuntimeEntityExpirySink
{
    private readonly RuntimeEntityObjectLifetime _entities;

    public RuntimeCanonicalEntityExpirySink(RuntimeEntityObjectLifetime entities) =>
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));

    public bool Expire(RuntimeEntityExpiryCandidate candidate)
    {
        if (!_entities.Entities.TryGetActive(candidate.ServerGuid, out RuntimeEntityRecord current)
            || current.Key != candidate.Key)
        {
            return false;
        }

        return DeleteCanonicalOnly(
            _entities,
            new DeleteObject.Parsed(candidate.ServerGuid, candidate.Generation));
    }

    public bool Destroy(DeleteObject.Parsed delete) =>
        DeleteCanonicalOnly(_entities, delete);

    /// <summary>
    /// Accepts and completes a delete for a host that holds no projection
    /// of the object. Shared with the session's own delete handling so an
    /// expiry and a server delete take the same path.
    /// </summary>
    internal static bool DeleteCanonicalOnly(
        RuntimeEntityObjectLifetime entities,
        DeleteObject.Parsed delete)
    {
        if (!entities.TryAcceptDelete(
                delete,
                isLocalPlayer: false,
                removeRetainedObject: true,
                out RuntimeEntityDeleteAcceptance acceptance))
        {
            return false;
        }

        entities.CompleteAcceptedDelete(acceptance);
        if (acceptance.RetiredCanonical is { } retired)
        {
            Exception? failure = entities.RetireCanonicalOnly(retired);
            if (failure is not null)
                throw failure;
        }
        return true;
    }
}

/// <summary>
/// Owns the 25-second destruction deadline for world objects that left
/// visibility, for every host. Outdoors visibility is the player's landblock
/// and its eight neighbours: the original client only ever holds those
/// cells, releases everything outside them, and the server mirrors the same
/// 3x3 as the player's known-object set, forgetting an object 25 s after it
/// leaves and re-sending it on return. Inside a sealed dungeon cell it is
/// the player's cell and that cell's authored visible-cell list, see
/// <see cref="RuntimeEntityVisibleCellSet"/>. An object held by a parent or
/// wielder in the world leaves and returns with its holder.
///
/// Objects outside the world (inside a container) are on an explicit
/// destruction queue with the same 25-second deadline, fed at the points the
/// original client feeds it: the contents of a container the player stops
/// looking at, the contents of a destroyed container, and an object the
/// server moves into a container the player neither owns, has open, nor is
/// trading with. A create for the object, or a move into the player's pack or
/// the open container, takes it off again. The server destroys container
/// contents without a message, so without this queue every corpse item ever
/// looked at would be kept for the whole session.
/// </summary>
internal sealed class RuntimeEntityLivenessController : IDisposable
{
    /// <summary>Landblock Chebyshev radius of the visible neighbourhood.</summary>
    internal const int VisibleLandblockRadius = 1;
    private const double MaintenanceIntervalSeconds = 1.0;

    private readonly RuntimeEntityObjectLifetime _entities;
    private readonly RuntimeLocalPlayerIdentityState _identity;
    private readonly IRuntimeEntityExpirySink _expiry;
    private readonly IRuntimeEntityCurrentCellSource _cells;
    private readonly ExternalContainerState? _groundObject;
    private readonly RuntimeTradeState? _trade;
    private readonly VendorState? _vendor;
    private readonly RuntimeEntityLivenessTracker _tracker = new();
    private readonly RuntimeEntityVisibleCellSet _visibleCells = new();
    private readonly List<RuntimeEntityLivenessSample> _samples = new();
    private readonly List<RuntimeEntityExpiryCandidate> _due = new();
    private readonly Dictionary<uint, double> _queuedObjects = [];
    private readonly List<uint> _dueObjects = [];
    // The viewed lists of containers nested in one the player closed. The
    // original client keeps such a list on its container until that
    // container is destroyed, and only then queues what it still holds.
    private readonly Dictionary<uint, IReadOnlyList<uint>> _nestedLists = [];
    private double _nextMaintenanceAt;
    private double _now;
    private bool _disposed;

    public RuntimeEntityLivenessController(
        RuntimeEntityObjectLifetime entities,
        RuntimeLocalPlayerIdentityState identity,
        IRuntimeEntityExpirySink expiry,
        IRuntimeEntityCurrentCellSource cells,
        ExternalContainerState? groundObject = null,
        RuntimeTradeState? trade = null,
        VendorState? vendor = null)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _expiry = expiry ?? throw new ArgumentNullException(nameof(expiry));
        _cells = cells ?? throw new ArgumentNullException(nameof(cells));
        _groundObject = groundObject;
        _trade = trade;
        _vendor = vendor;

        ClientObjectTable objects = _entities.Objects;
        objects.ContentsViewEnded += OnContentsViewEnded;
        objects.ObjectRemovalClassified += OnObjectRemoved;
        objects.ObjectMoved += OnObjectMoved;
        objects.Cleared += OnObjectsCleared;
        _entities.SpawnApplied += OnSpawnApplied;
        if (_trade is not null)
        {
            _trade.PartnerItemAdded += OnPartnerItemAdded;
            _trade.PartnerItemsReleased += OnPartnerItemsReleased;
        }
        if (_vendor is not null)
            _vendor.Changed += OnVendorChanged;
    }

    /// <summary>Objects outside the world waiting on their deadline.</summary>
    internal int QueuedObjectCount => _queuedObjects.Count;

    public void Tick(double now)
    {
        _now = now;
        if (now < _nextMaintenanceAt)
            return;
        _nextMaintenanceAt = now + MaintenanceIntervalSeconds;

        DestroyQueuedObjects(now);

        RuntimeEntityDirectory directory = _entities.Entities;
        uint playerGuid = _identity.ServerGuid;
        if (playerGuid == 0u
            || !directory.TryGetActive(playerGuid, out RuntimeEntityRecord player))
        {
            return;
        }
        uint playerCell = CellOf(player);
        if (playerCell == 0u)
            return;
        _visibleCells.Update(_cells.CurrentCell, playerCell);

        _samples.Clear();
        foreach (RuntimeEntityRecord record in directory.ActiveRecords)
        {
            if (record.ServerGuid == playerGuid || record.Key is not { } key)
                continue;

            // Placement is read from the object table, which server moves
            // keep current; the create-time snapshot still names the
            // wielder of a weapon looted off a corpse long ago.
            ClientObject? placed = _entities.Objects.Get(record.ServerGuid);
            uint containerId = placed?.ContainerId
                ?? record.Snapshot.ContainerId.GetValueOrDefault();
            if (containerId != 0u)
            {
                // Container contents are on the explicit queue instead.
                _samples.Add(new RuntimeEntityLivenessSample(
                    key,
                    record.ServerGuid,
                    IsConservativelyVisible: true,
                    HasNonWorldRetention: true));
                continue;
            }

            if (TryGetHolder(directory, record, placed, out uint holderGuid))
            {
                // A held object is queued and unqueued with its holder. A
                // holder that is gone, or is not in the world itself, keeps
                // it: a deleted holder only detaches what it held.
                if (holderGuid != playerGuid
                    && directory.TryGetActive(holderGuid, out RuntimeEntityRecord holder)
                    && CellOf(holder) is var holderCell and not 0u)
                {
                    _samples.Add(new RuntimeEntityLivenessSample(
                        key,
                        record.ServerGuid,
                        _visibleCells.Contains(holderCell),
                        HasNonWorldRetention: false));
                }
                else
                {
                    _samples.Add(new RuntimeEntityLivenessSample(
                        key,
                        record.ServerGuid,
                        IsConservativelyVisible: true,
                        HasNonWorldRetention: true));
                }
                continue;
            }

            uint cell = CellOf(record);
            if (cell == 0u)
                continue;
            _samples.Add(new RuntimeEntityLivenessSample(
                key,
                record.ServerGuid,
                _visibleCells.Contains(cell),
                HasNonWorldRetention: false));
        }

        // Expiry mutates the directory; copy the due list first.
        _due.Clear();
        _due.AddRange(_tracker.Tick(now, _samples));
        for (int i = 0; i < _due.Count; i++)
        {
            RuntimeEntityExpiryCandidate candidate = _due[i];
            if (directory.TryGetActive(candidate.ServerGuid, out RuntimeEntityRecord current)
                && current.Key == candidate.Key)
            {
                _expiry.Expire(candidate);
            }
        }
    }

    public void Clear()
    {
        _tracker.Clear();
        _samples.Clear();
        _due.Clear();
        _queuedObjects.Clear();
        _dueObjects.Clear();
        _nestedLists.Clear();
        _visibleCells.Update(null, 0u);
        _nextMaintenanceAt = 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ClientObjectTable objects = _entities.Objects;
        objects.ContentsViewEnded -= OnContentsViewEnded;
        objects.ObjectRemovalClassified -= OnObjectRemoved;
        objects.ObjectMoved -= OnObjectMoved;
        objects.Cleared -= OnObjectsCleared;
        _entities.SpawnApplied -= OnSpawnApplied;
        if (_trade is not null)
        {
            _trade.PartnerItemAdded -= OnPartnerItemAdded;
            _trade.PartnerItemsReleased -= OnPartnerItemsReleased;
        }
        if (_vendor is not null)
            _vendor.Changed -= OnVendorChanged;
    }

    private void DestroyQueuedObjects(double now)
    {
        _dueObjects.Clear();
        foreach ((uint guid, double expiresAt) in _queuedObjects)
        {
            if (expiresAt <= now)
                _dueObjects.Add(guid);
        }

        // A destroyed container queues its own contents; that mutates the
        // queue, so the due list is copied first.
        for (int i = 0; i < _dueObjects.Count; i++)
        {
            uint guid = _dueObjects[i];
            if (!_queuedObjects.Remove(guid))
                continue;
            DestroyQueuedObject(guid);
        }
    }

    private void DestroyQueuedObject(uint guid)
    {
        if (guid == _identity.ServerGuid)
            return;

        if (_entities.Entities.TryGetActive(guid, out RuntimeEntityRecord record))
        {
            _expiry.Destroy(new DeleteObject.Parsed(guid, record.Incarnation));
            return;
        }

        // Known only from a container's contents list, never created.
        _entities.Objects.Remove(guid);
    }

    private void OnContentsViewEnded(ClientObjectContentsViewEnd end)
    {
        if (end.IsNested)
        {
            _nestedLists[end.ContainerId] = end.Contents.ToArray();
            return;
        }
        for (int i = 0; i < end.Contents.Count; i++)
            Enqueue(end.Contents[i]);
    }

    private void OnObjectRemoved(ClientObjectRemoval removal)
    {
        uint guid = removal.Object.ObjectId;
        _queuedObjects.Remove(guid);
        if (removal.Reason == ClientObjectRemovalReason.GenerationReplacement)
            return;

        // What the destroyed container still holds, and is not in the world
        // itself, follows it 25 seconds later.
        ClientObjectTable objects = _entities.Objects;
        IReadOnlyList<uint> contents = ViewedContents(guid);
        _nestedLists.Remove(guid);
        for (int i = 0; i < contents.Count; i++)
        {
            uint member = contents[i];
            ClientObject? item = objects.Get(member);
            if (item is not null && item.ContainerId != guid)
                continue;
            if (_entities.Entities.TryGetActive(member, out RuntimeEntityRecord record)
                && record.FullCellId != 0u)
            {
                continue;
            }
            Enqueue(member);
        }
    }

    /// <summary>
    /// The partner put an item into the trade: it and what it holds stay.
    /// </summary>
    private void OnPartnerItemAdded(uint guid)
    {
        Dequeue(guid);
        DequeueContents(guid, depth: 0);
    }

    /// <summary>
    /// The partner's items left the trade window. What they hold is queued;
    /// the items themselves stay where the server put them.
    /// </summary>
    private void OnPartnerItemsReleased(IReadOnlyList<uint> items)
    {
        ClientObjectTable objects = _entities.Objects;
        uint playerGuid = _identity.ServerGuid;
        for (int i = 0; i < items.Count; i++)
        {
            uint guid = items[i];
            if (objects.Get(guid) is not { } item
                || objects.IsOwnedByObject(guid, playerGuid)
                || item.WielderId != 0u
                || item.CurrentlyEquippedLocation != EquipMask.None)
            {
                continue;
            }
            EnqueueContents(guid);
        }
    }

    /// <summary>A vendor's window shows its items, so none of them go.</summary>
    private void OnVendorChanged(VendorTransition transition)
    {
        if (_vendor is null)
            return;
        IReadOnlyList<VendorShopItem> items = _vendor.Items;
        for (int i = 0; i < items.Count; i++)
            Dequeue(items[i].ItemGuid);
    }

    private IReadOnlyList<uint> ViewedContents(uint containerId)
    {
        IReadOnlyList<uint> viewed = _entities.Objects.GetContents(containerId);
        if (viewed.Count != 0)
            return viewed;
        return _nestedLists.TryGetValue(containerId, out IReadOnlyList<uint>? nested)
            ? nested
            : viewed;
    }

    private void OnObjectMoved(ClientObjectMove move)
    {
        if (move.Origin != ClientObjectMoveOrigin.AuthoritativeResponse
            || move.Current.ContainerId == 0u
            || move.Item is null)
        {
            return;
        }

        uint guid = move.ItemId;
        if (IsKeptWhereItIs(guid))
        {
            Dequeue(guid);
            DequeueContents(guid, depth: 0);
        }
        else
        {
            EnqueueContents(guid);
            Enqueue(guid);
        }
    }

    private void OnSpawnApplied(uint guid)
    {
        Dequeue(guid);
        IReadOnlyList<uint> contents = ViewedContents(guid);
        for (int i = 0; i < contents.Count; i++)
            Dequeue(contents[i]);
    }

    private void OnObjectsCleared()
    {
        _queuedObjects.Clear();
        _nestedLists.Clear();
    }

    /// <summary>
    /// Owned by the player, inside the container the player has open, or
    /// part of the open trade.
    /// </summary>
    private bool IsKeptWhereItIs(uint guid)
    {
        ClientObjectTable objects = _entities.Objects;
        uint playerGuid = _identity.ServerGuid;
        if (playerGuid != 0u && objects.IsOwnedByObject(guid, playerGuid))
            return true;

        uint groundObject = _groundObject?.CurrentContainerId ?? 0u;
        if (groundObject != 0u && objects.IsOwnedByObject(guid, groundObject))
            return true;

        if (_trade?.View is { } trade)
        {
            RuntimeTradeSnapshot snapshot = trade.Snapshot;
            if (snapshot.IsOpen
                && snapshot.PartnerGuid != 0u
                && (objects.IsOwnedByObject(guid, snapshot.PartnerGuid)
                    || trade.GetItems(RuntimeTradeSide.Partner).Contains(guid)))
            {
                return true;
            }
        }
        return false;
    }

    private void EnqueueContents(uint containerId)
    {
        IReadOnlyList<uint> contents = ViewedContents(containerId);
        for (int i = 0; i < contents.Count; i++)
            Enqueue(contents[i]);
    }

    private void DequeueContents(uint containerId, int depth)
    {
        // Containers nest one level in practice; the bound only guards a
        // malformed list that names its own ancestor.
        if (depth > 8)
            return;
        IReadOnlyList<uint> contents = ViewedContents(containerId);
        for (int i = 0; i < contents.Count; i++)
        {
            Dequeue(contents[i]);
            DequeueContents(contents[i], depth + 1);
        }
    }

    private void Enqueue(uint guid)
    {
        if (guid == 0u || guid == _identity.ServerGuid)
            return;
        _queuedObjects[guid] = _now + RuntimeEntityLivenessTracker.DestructionTimeoutSeconds;
    }

    private void Dequeue(uint guid) => _queuedObjects.Remove(guid);

    /// <summary>
    /// The object this record hangs from in the world: its physics parent,
    /// else its wielder, else its declared parent.
    /// </summary>
    private static bool TryGetHolder(
        RuntimeEntityDirectory directory,
        RuntimeEntityRecord record,
        ClientObject? placed,
        out uint holderGuid)
    {
        if (directory.ParentAttachments.TryGetCommittedParent(
                record.ServerGuid,
                out holderGuid,
                out _))
        {
            return true;
        }
        holderGuid = placed?.WielderId
            ?? record.Snapshot.WielderId.GetValueOrDefault();
        if (holderGuid != 0u)
            return true;
        holderGuid = record.Snapshot.ParentGuid.GetValueOrDefault();
        return holderGuid != 0u;
    }

    /// <summary>
    /// True when <paramref name="entityCell"/>'s landblock is within one
    /// landblock (Chebyshev) of <paramref name="playerCell"/>'s. Both are
    /// cell ids; only the landblock halves matter.
    /// </summary>
    internal static bool IsWithinVisibleLandblocks(uint playerCell, uint entityCell)
    {
        int dx = Math.Abs((int)((playerCell >> 24) & 0xFFu) - (int)((entityCell >> 24) & 0xFFu));
        int dy = Math.Abs((int)((playerCell >> 16) & 0xFFu) - (int)((entityCell >> 16) & 0xFFu));
        return Math.Max(dx, dy) <= VisibleLandblockRadius;
    }

    /// <summary>The record's committed cell, else its accepted spawn cell, else 0.</summary>
    private static uint CellOf(RuntimeEntityRecord record)
    {
        if (record.FullCellId != 0u)
            return record.FullCellId;
        return record.Snapshot.Position?.LandblockId ?? 0u;
    }

    private static bool NonZero(uint? value) => value.GetValueOrDefault() != 0u;
}
