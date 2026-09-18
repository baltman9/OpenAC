using AcDream.App.Input;
using AcDream.Runtime.Entities;

namespace AcDream.App.World;

internal readonly record struct LiveEntityLivenessSample(
    RuntimeEntityKey Key,
    uint ServerGuid,
    bool IsConservativelyVisible,
    bool HasNonWorldRetention);

internal readonly record struct LiveEntityPruneCandidate(
    RuntimeEntityKey Key,
    uint ServerGuid,
    ushort Generation)
{
    public LiveEntityPruneCandidate(RuntimeEntityKey key, uint serverGuid)
        : this(key, serverGuid, key.Incarnation)
    {
    }
}

internal sealed class LiveEntityLivenessTracker
{
    internal const double DestructionTimeoutSeconds = 25.0;

    private readonly Dictionary<RuntimeEntityKey, double> _deadlines = [];
    private readonly HashSet<RuntimeEntityKey> _present = [];
    private readonly List<RuntimeEntityKey> _stale = [];
    private readonly List<LiveEntityPruneCandidate> _due = [];

    internal int DeadlineCount => _deadlines.Count;

    internal IReadOnlyList<LiveEntityPruneCandidate> Tick(
        double now,
        IReadOnlyList<LiveEntityLivenessSample> samples)
    {
        _present.Clear();
        _due.Clear();
        for (int i = 0; i < samples.Count; i++)
        {
            LiveEntityLivenessSample sample = samples[i];
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

            _due.Add(new LiveEntityPruneCandidate(sample.Key, sample.ServerGuid));
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
/// Owns the 25-second destruction deadline for world objects that left
/// visibility. Visibility is the player's landblock and its eight
/// neighbours (a dungeon is its own landblock): the original client only
/// ever holds those cells, releases everything outside them, and the server
/// mirrors the same 3x3 as the player's known-object set, forgetting an
/// object 25 s after it leaves and re-sending it on return. Objects held by
/// a container, wielder, or parent, and attached projections, never expire.
/// </summary>
/// <summary>
/// Holds the objects whose destruction deadline has fallen due and hands them
/// out a few per frame.
///
/// Every object of a landblock the player left starts its twenty-five second
/// deadline within the same second, so a quarter of a minute later they all
/// fall due in one maintenance tick. Destroying one object is a full
/// lifetime transaction -- accept, complete, tear down, forget -- and at a
/// dense spot seventy of them in one frame cost twenty-three milliseconds
/// against a five millisecond frame.
///
/// Which frame inside the next fraction of a second destroys them is not
/// observable: they are outside the visible landblocks, which is why they
/// expired, nothing draws them, and their destruction sends nothing. The
/// first few still go in the tick that found them, so an ordinary expiry of
/// one or two objects behaves exactly as before.
/// </summary>
internal sealed class LiveEntityPruneBacklog
{
    /// <summary>Objects destroyed per frame. Four keeps the added frame cost
    /// near a millisecond at the densest spot measured, and drains a whole
    /// landblock's expiry inside a fifth of a second at sixty frames.</summary>
    internal const int MaxPerFrame = 4;

    private readonly Queue<LiveEntityPruneCandidate> _pending = new();

    internal int Count => _pending.Count;

    internal void Add(IReadOnlyList<LiveEntityPruneCandidate> due)
    {
        ArgumentNullException.ThrowIfNull(due);
        for (int i = 0; i < due.Count; i++)
            _pending.Enqueue(due[i]);
    }

    /// <summary>Moves up to <see cref="MaxPerFrame"/> candidates into
    /// <paramref name="destination"/>, oldest deadline first.</summary>
    internal void TakeFrameBatch(List<LiveEntityPruneCandidate> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Clear();
        while (destination.Count < MaxPerFrame
               && _pending.TryDequeue(out LiveEntityPruneCandidate candidate))
        {
            destination.Add(candidate);
        }
    }

    internal void Clear() => _pending.Clear();
}

internal sealed class LiveEntityLivenessController
{
    /// <summary>Landblock Chebyshev radius of the visible neighbourhood.</summary>
    internal const int VisibleLandblockRadius = 1;
    private const double MaintenanceIntervalSeconds = 1.0;

    private readonly LiveEntityRuntime _runtime;
    private readonly ILocalPlayerIdentitySource _identity;
    private readonly ILiveEntityPruneSink _prune;
    private readonly LiveEntityLivenessTracker _tracker = new();
    private readonly List<LiveEntityLivenessSample> _samples = new();
    private readonly LiveEntityPruneBacklog _backlog = new();
    private readonly List<LiveEntityPruneCandidate> _frameBatch = new();
    private double _nextMaintenanceAt;

    public LiveEntityLivenessController(
        LiveEntityRuntime runtime,
        ILocalPlayerIdentitySource identity,
        ILiveEntityPruneSink prune)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _prune = prune ?? throw new ArgumentNullException(nameof(prune));
    }

    public void Tick(double now)
    {
        // The backlog drains every frame, not only on a maintenance tick.
        DrainBacklog();
        if (now < _nextMaintenanceAt)
            return;
        _nextMaintenanceAt = now + MaintenanceIntervalSeconds;

        uint playerGuid = _identity.ServerGuid;
        if (playerGuid == 0
            || !_runtime.TryGetRecord(playerGuid, out LiveEntityRecord player))
        {
            return;
        }
        uint playerCell = CellOf(player);
        if (playerCell == 0u)
            return;

        _samples.Clear();
        foreach (LiveEntityRecord record in _runtime.Records)
        {
            if (record.ServerGuid == playerGuid)
                continue;
            uint cell = CellOf(record);
            if (cell == 0u)
                continue;

            bool retained = record.ProjectionKind is LiveEntityProjectionKind.Attached
                || NonZero(record.Snapshot.ContainerId)
                || NonZero(record.Snapshot.WielderId)
                || NonZero(record.Snapshot.ParentGuid);
            _samples.Add(new LiveEntityLivenessSample(
                record.ProjectionKey
                    ?? throw new InvalidOperationException(
                        $"Materialized liveness owner 0x{record.ServerGuid:X8}/" +
                        $"{record.Generation} has no exact projection key."),
                record.ServerGuid,
                IsWithinVisibleLandblocks(playerCell, cell),
                retained));
        }

        _backlog.Add(_tracker.Tick(now, _samples));
        DrainBacklog();
    }

    private void DrainBacklog()
    {
        if (_backlog.Count == 0)
            return;

        _backlog.TakeFrameBatch(_frameBatch);
        for (int i = 0; i < _frameBatch.Count; i++)
        {
            LiveEntityPruneCandidate candidate = _frameBatch[i];
            if (_runtime.TryGetRecord(candidate.Key, out LiveEntityRecord current)
                && current.ServerGuid == candidate.ServerGuid)
            {
                _prune.Prune(candidate);
            }
        }
    }

    public void Clear()
    {
        _tracker.Clear();
        _samples.Clear();
        _backlog.Clear();
        _frameBatch.Clear();
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
    private static uint CellOf(LiveEntityRecord record)
    {
        if (record.FullCellId != 0u)
            return record.FullCellId;
        return record.Snapshot.Position?.LandblockId ?? 0u;
    }

    private static bool NonZero(uint? value) => value.GetValueOrDefault() != 0u;
}
