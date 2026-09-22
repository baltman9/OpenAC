using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeAllegianceOwnershipSnapshot(
    bool IsDisposed,
    bool HasProfile,
    int RecordCount)
{
    public bool IsConverged =>
        IsDisposed
        && !HasProfile
        && RecordCount == 0;
}

public sealed class RuntimeAllegianceState : IDisposable
{
    private readonly object _gate = new();
    private ClientCommandResponses.AllegianceMemberRecord? _monarch;
    private IReadOnlyList<ClientCommandResponses.AllegianceMemberRecord> _records =
        [];
    private string _allegianceName = string.Empty;
    private uint _totalMembers;
    private uint _totalVassals;
    private uint _rank;
    private bool _hasProfile;
    private bool _hasServerSeed;
    private bool _askedOnEntry;
    private long _revision;
    private bool _disposed;

    public RuntimeAllegianceState() => View = new AllegianceView(this);

    public IRuntimeAllegianceView View { get; }

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    public bool HasServerSeed
    {
        get { lock (_gate) return _hasServerSeed; }
    }

    /// <summary>
    /// The character has just arrived in the world, which is when the client
    /// asks the server to state the allegiance: nothing here is known until
    /// the server does, and the server only speaks about an allegiance when
    /// it is asked. The original client asks at exactly this moment -- as
    /// soon as the character's own description has arrived -- rather than
    /// waiting for anyone to open a panel, which is why an allegiance is
    /// there to read the instant a player logs in.
    /// </summary>
    /// <returns>
    /// True the first time in a session, so the caller sends the request
    /// once. A new generation clears it, so a reconnect asks again.
    /// </returns>
    public bool NoteEnteredWorld()
    {
        lock (_gate)
        {
            if (_disposed)
                return false;
            if (_askedOnEntry)
                return false;
            _askedOnEntry = true;
            return true;
        }
    }

    public void ApplyUpdate(ClientCommandResponses.AllegianceUpdate update)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _monarch = update.Monarch;
            _records = update.Records;
            _allegianceName = update.AllegianceName;
            _totalMembers = update.TotalMembers;
            _totalVassals = update.TotalVassals;
            _rank = update.Rank;
            _hasProfile = true;
            _hasServerSeed = true;
            Bump();
        }
    }

    public void ApplyLoginNotification(uint characterGuid, bool isLoggedIn)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Bump();
        }
    }

    public void ApplyUpdateDone(uint weenieError)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Bump();
        }
    }

    public void ApplyUpdateAborted(uint weenieError)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Bump();
        }
    }

    public RuntimeAllegianceOwnershipSnapshot CaptureOwnership()
    {
        lock (_gate)
            return new RuntimeAllegianceOwnershipSnapshot(
                _disposed,
                _hasProfile,
                _records.Count);
    }

    public void ResetSession()
    {
        lock (_gate) ClearLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ClearLocked();
            _disposed = true;
        }
    }

    private void ClearLocked()
    {
        bool changed = _monarch is not null
            || _records.Count != 0
            || _allegianceName.Length != 0
            || _totalMembers != 0u
            || _totalVassals != 0u
            || _rank != 0u
            || _hasProfile
            || _hasServerSeed;
        _monarch = null;
        _records = [];
        _allegianceName = string.Empty;
        _totalMembers = 0u;
        _totalVassals = 0u;
        _rank = 0u;
        _hasProfile = false;
        _hasServerSeed = false;
        // A new session asks again: nothing carried over is worth keeping,
        // and the ask is the only way anything comes back.
        _askedOnEntry = false;
        if (changed) Bump();
    }

    private void Bump() => _revision++;

    private sealed class AllegianceView(RuntimeAllegianceState owner)
        : IRuntimeAllegianceView
    {
        public RuntimeAllegianceSnapshot Snapshot
        {
            get
            {
                lock (owner._gate)
                    return new RuntimeAllegianceSnapshot(
                        owner._revision,
                        owner._hasServerSeed,
                        owner._hasProfile,
                        owner._rank,
                        owner._totalMembers,
                        owner._totalVassals,
                        owner._allegianceName,
                        owner._monarch?.CharacterId ?? 0u,
                        owner._records.Count);
            }
        }

        public bool TryGetMonarch(out RuntimeAllegianceMemberSnapshot monarch)
        {
            lock (owner._gate)
            {
                if (owner._monarch is not { } record)
                {
                    monarch = default;
                    return false;
                }
                monarch = ToSnapshot(record);
                return true;
            }
        }

        public bool TryGetMember(uint guid, out RuntimeAllegianceMemberSnapshot member)
        {
            lock (owner._gate)
            {
                ClientCommandResponses.AllegianceMemberRecord? record =
                    ClientCommandResponses.AllegianceProfileLookups.FindData(
                        owner._monarch, owner._records, guid);
                if (record is not { } found)
                {
                    member = default;
                    return false;
                }
                member = ToSnapshot(found);
                return true;
            }
        }

        public bool TryGetPatron(uint guid, out RuntimeAllegianceMemberSnapshot patron)
        {
            lock (owner._gate)
            {
                ClientCommandResponses.AllegianceMemberRecord? record =
                    ClientCommandResponses.AllegianceProfileLookups.FindPatron(
                        owner._monarch, owner._records, guid);
                if (record is not { } found)
                {
                    patron = default;
                    return false;
                }
                patron = ToSnapshot(found);
                return true;
            }
        }

        public IEnumerable<RuntimeAllegianceMemberSnapshot> GetVassals(uint guid)
        {
            List<RuntimeAllegianceMemberSnapshot> result;
            lock (owner._gate)
            {
                result = new List<RuntimeAllegianceMemberSnapshot>();
                foreach (ClientCommandResponses.AllegianceMemberRecord record in
                    ClientCommandResponses.AllegianceProfileLookups.FindVassals(owner._records, guid))
                {
                    result.Add(ToSnapshot(record));
                }
            }
            return result;
        }

        private static RuntimeAllegianceMemberSnapshot ToSnapshot(
            ClientCommandResponses.AllegianceMemberRecord record) =>
            new(
                record.CharacterId,
                record.ParentGuid,
                record.IsLoggedIn,
                record.Name,
                record.Rank,
                record.Level,
                record.Loyalty,
                record.Leadership,
                record.CpCached,
                record.CpTithed,
                record.Gender,
                record.HeritageGroup,
                record.MayPassupExperience);
    }
}
