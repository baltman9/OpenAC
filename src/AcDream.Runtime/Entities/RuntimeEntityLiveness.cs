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
/// <see cref="RuntimeEntityVisibleCellSet"/>. Objects held by a container,
/// wielder, or parent never expire.
/// </summary>
internal sealed class RuntimeEntityLivenessController
{
    /// <summary>Landblock Chebyshev radius of the visible neighbourhood.</summary>
    internal const int VisibleLandblockRadius = 1;
    private const double MaintenanceIntervalSeconds = 1.0;

    private readonly RuntimeEntityObjectLifetime _entities;
    private readonly RuntimeLocalPlayerIdentityState _identity;
    private readonly IRuntimeEntityExpirySink _expiry;
    private readonly IRuntimeEntityCurrentCellSource _cells;
    private readonly RuntimeEntityLivenessTracker _tracker = new();
    private readonly RuntimeEntityVisibleCellSet _visibleCells = new();
    private readonly List<RuntimeEntityLivenessSample> _samples = new();
    private readonly List<RuntimeEntityExpiryCandidate> _due = new();
    private double _nextMaintenanceAt;

    public RuntimeEntityLivenessController(
        RuntimeEntityObjectLifetime entities,
        RuntimeLocalPlayerIdentityState identity,
        IRuntimeEntityExpirySink expiry,
        IRuntimeEntityCurrentCellSource cells)
    {
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _expiry = expiry ?? throw new ArgumentNullException(nameof(expiry));
        _cells = cells ?? throw new ArgumentNullException(nameof(cells));
    }

    public void Tick(double now)
    {
        if (now < _nextMaintenanceAt)
            return;
        _nextMaintenanceAt = now + MaintenanceIntervalSeconds;

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
            if (record.ServerGuid == playerGuid)
                continue;
            uint cell = CellOf(record);
            if (cell == 0u || record.Key is not { } key)
                continue;

            bool retained = directory.ParentAttachments.HasCommittedParent(record.ServerGuid)
                || NonZero(record.Snapshot.ContainerId)
                || NonZero(record.Snapshot.WielderId)
                || NonZero(record.Snapshot.ParentGuid);
            _samples.Add(new RuntimeEntityLivenessSample(
                key,
                record.ServerGuid,
                _visibleCells.Contains(cell),
                retained));
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
        _visibleCells.Update(null, 0u);
        _nextMaintenanceAt = 0;
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
