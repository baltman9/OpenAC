using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Plugins;

/// <summary>
/// One cast this client wants the other clients on this computer to know
/// about, before the registry stamps it with a sequence and a time.
/// </summary>
/// <param name="CasterObjectId">The character that cast it.</param>
/// <param name="TargetObjectId">The object it was cast at.</param>
/// <param name="SpellId">The spell, from this client's own spell table.</param>
/// <param name="EffectiveSkill">The skill it was cast with.</param>
/// <param name="DurationSeconds">
/// How long the effect lasts in total, or zero for an attempt that has not
/// landed yet.
/// </param>
/// <param name="Landed">True when the cast is known to have landed.</param>
internal readonly record struct LocalPluginCast(
    uint CasterObjectId,
    uint TargetObjectId,
    uint SpellId,
    int EffectiveSkill,
    double DurationSeconds,
    bool Landed);

internal sealed class LocalPluginPeerRegistry : IDisposable
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many casts one client's note carries. The note is a snapshot --
    /// the whole document is replaced on every write, last writer wins -- so
    /// a cast kept in a single field would be lost by the next heartbeat. A
    /// ring survives that: a reader polling slower than the writer still sees
    /// every cast, as long as no more than this many happen between two of
    /// its reads. Thirty-two is far more than one caster can produce inside
    /// the staleness window, and thirty-two of these entries is a couple of
    /// kilobytes against the document cap below.
    /// </summary>
    internal const int CastRingCapacity = 32;

    /// <summary>
    /// How many casts read from other clients this one keeps to hand back.
    /// Several plugins share one client, each reading with a cursor of its
    /// own, so a read cannot consume: one plugin polling would starve the
    /// next. Entries leave by age as well, so this is only a ceiling for a
    /// burst nobody has read yet.
    /// </summary>
    internal const int ObservedCastCapacity = 128;

    /// <summary>
    /// The shortest gap between two notes written because of a cast. The
    /// first cast after a quiet moment is written at once; the ones behind it
    /// in a burst ride along on the next write. Without it a six-target
    /// debuff chain would rewrite the whole document six times inside a few
    /// hundred milliseconds.
    /// </summary>
    internal static readonly TimeSpan CastWriteDebounce =
        TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How often the note is rewritten when nothing in particular is
    /// happening -- the hosts tick on this period -- and how long this client
    /// holds off asking for another write after one it could not do. The two
    /// are the same number on purpose: a write that failed or was refused
    /// costs exactly what the heartbeat costs anyway, instead of being retried
    /// on every frame because the ring still has something waiting.
    /// </summary>
    internal static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest effect a cast may claim, in seconds. A day is far beyond
    /// anything a spell lasts, so a longer one is a broken or hostile note
    /// rather than a long buff, and a reader that believed it would hold a
    /// target as enchanted for ever.
    /// </summary>
    internal const double MaximumCastDurationSeconds = 24d * 60d * 60d;

    /// <summary>
    /// The highest sequence a cast may claim. Sequences count from one and
    /// only ever grow, and a reader sets its cursor for a peer from them, so
    /// a note claiming a number no client could have counted to would park
    /// that cursor past everything the genuine client will ever send. A
    /// thousand million is years of casting several times a second, and a
    /// note above it is broken or hostile rather than long-lived.
    /// </summary>
    internal const long MaximumCastSequence = 1_000_000_000L;

    /// <summary>
    /// The earliest and latest instants a cast may be stamped with. A note is
    /// a file written by another process, so its stamp is any eight bytes at
    /// all, and a number outside this range is not a time: converting it
    /// throws rather than returning something absurd, and that throw would
    /// come out of the plugin's own capture call. The window rule below
    /// narrows this to a few seconds; this pair only keeps the arithmetic
    /// legal.
    /// </summary>
    private static readonly long EarliestCastUnixMs =
        DateTimeOffset.MinValue.ToUnixTimeMilliseconds();

    private static readonly long LatestCastUnixMs =
        DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private const long MaximumDocumentBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly Guid _instanceId;
    private readonly object _gate = new();

    /// <summary>What this client is telling the others, oldest first.</summary>
    private readonly List<PeerCastEntry> _ring = [];

    /// <summary>The highest sequence taken in from each peer's note.</summary>
    private readonly Dictionary<PeerCursorKey, ObservedPeer> _observedPeers = [];

    /// <summary>What this client has read from the others, oldest first.</summary>
    private readonly List<ObservedCast> _observedCasts = [];

    private long _castSequence;
    private long _observedSequence;
    private bool _castWritePending;
    private DateTimeOffset? _lastWriteAt;

    /// <summary>
    /// When a write that failed or was refused stops holding the next one
    /// back. Null when the last attempt got the note out.
    /// </summary>
    private DateTimeOffset? _holdWritesUntil;
    private bool _disposed;

    public LocalPluginPeerRegistry(
        string directory,
        TimeProvider? timeProvider = null,
        Guid? instanceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _time = timeProvider ?? TimeProvider.System;
        _instanceId = instanceId ?? Guid.NewGuid();
        _path = Path.Combine(_directory, $"peer-{_instanceId:N}.json");
        ClientId = BitConverter.ToUInt32(_instanceId.ToByteArray(), 0);
        if (ClientId == 0u)
            ClientId = 1u;
    }

    public uint ClientId { get; private set; }

    /// <summary>
    /// Writes this client's note, replacing whatever it said before.
    /// </summary>
    /// <returns>
    /// False when the note was refused rather than written, because this
    /// client's own position or heading is not a finite number and such a
    /// note cannot be written as JSON at all. Every reader refuses a note
    /// like that, so refusing it here costs nothing and keeps the failure
    /// out of the caller, which is a plugin tick.
    /// </returns>
    public bool Publish(in PluginNetworkClient client)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = _time.GetUtcNow();
        if (!CanBeWritten(client))
        {
            HoldOffAfterWriteThatDidNotHappen(now);
            return false;
        }
        PeerDocument document;
        long publishedThrough;
        lock (_gate)
        {
            publishedThrough = _castSequence;
            document = PeerDocument.From(
                client with { ClientId = ClientId },
                _instanceId,
                now.ToUnixTimeMilliseconds(),
                [.. _ring]);
        }
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool written = false;
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
            written = true;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            if (written)
            {
                // The note carries the whole ring, so any write -- a
                // heartbeat as much as a cast -- is what the debounce is
                // counting from. A cast recorded WHILE the file was being
                // written is not in the note that just went out, so it stays
                // waiting rather than being cleared with the ones that did.
                lock (_gate)
                {
                    if (_castSequence == publishedThrough)
                        _castWritePending = false;
                    _lastWriteAt = now;
                    _holdWritesUntil = null;
                }
            }
            else
            {
                // The ring still has something waiting and the last-write
                // stamp has not moved, so without this the write stays due
                // and the host's tick, which skips its early-out whenever a
                // write is due, would attempt one on every frame.
                HoldOffAfterWriteThatDidNotHappen(now);
            }
        }
        return true;
    }

    private void HoldOffAfterWriteThatDidNotHappen(DateTimeOffset now)
    {
        lock (_gate)
            _holdWritesUntil = now + HeartbeatPeriod;
    }

    /// <summary>
    /// Whether this client's own state can go in a note at all. A position or
    /// heading that is not a finite number cannot be written as JSON -- the
    /// writer refuses not-a-number and infinity outright -- and a reader
    /// refuses a note carrying one anyway. These are the only numbers in the
    /// document that are not whole; a cast's duration is held to the same
    /// rule on its way into the ring.
    /// </summary>
    private static bool CanBeWritten(in PluginNetworkClient client) =>
        double.IsFinite(client.Position.EastWest)
        && double.IsFinite(client.Position.NorthSouth)
        && double.IsFinite(client.Position.Elevation)
        && float.IsFinite(client.Heading);

    /// <summary>
    /// Adds a cast to the ring this client publishes. The note itself is not
    /// written here: the caller asks <see cref="IsCastWriteDue"/> and writes
    /// when the debounce allows, so a burst of casts costs one or two writes
    /// instead of one each.
    /// </summary>
    /// <returns>
    /// False for a cast that makes no sense, which is left out of the ring
    /// entirely. This is not only tidiness: a duration that is not a finite
    /// number cannot be written as JSON at all, so one such entry would make
    /// every later write of the whole note throw and this client would go
    /// silent for the rest of the session.
    /// </returns>
    public bool RecordCast(in LocalPluginCast cast)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long atUnixMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_gate)
        {
            var entry = new PeerCastEntry
            {
                Sequence = _castSequence + 1L,
                AtUnixMs = atUnixMs,
                CasterObjectId = cast.CasterObjectId,
                TargetObjectId = cast.TargetObjectId,
                SpellId = cast.SpellId,
                EffectiveSkill = cast.EffectiveSkill,
                DurationSeconds = cast.DurationSeconds,
                Landed = cast.Landed,
            };
            if (!IsWellFormed(entry))
                return false;
            _castSequence = entry.Sequence;
            _ring.Add(entry);
            if (_ring.Count > CastRingCapacity)
                _ring.RemoveRange(0, _ring.Count - CastRingCapacity);
            _castWritePending = true;
            return true;
        }
    }

    /// <summary>
    /// Whether a recorded cast is waiting to be published and enough time has
    /// passed since the last write to publish it. False while a write that
    /// could not be done is being held off: that one is retried on the
    /// heartbeat, not on the debounce and not on the frame.
    /// </summary>
    public bool IsCastWriteDue()
    {
        if (_disposed)
            return false;
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_holdWritesUntil is { } until && now < until)
                return false;
            return _castWritePending
                && (_lastWriteAt is not { } last
                    || now - last >= CastWriteDebounce);
        }
    }

    public IReadOnlyList<PluginNetworkClient> CaptureRemoteClients()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            return ReadRemoteNotes(_time.GetUtcNow())
                .Select(static note => note.Document.ToClient())
                .OrderBy(static client => client.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static client => client.ClientId)
                .ToArray();
        }
    }

    /// <summary>
    /// The casts other clients on this computer have reported that this one
    /// has not handed back yet under a sequence above
    /// <paramref name="afterSequence"/>.
    /// </summary>
    /// <param name="afterSequence">
    /// The highest sequence the caller has already dealt with; zero for
    /// everything still inside the staleness window.
    /// </param>
    /// <param name="worldName">
    /// The world this client is logged in to. A peer that names a different
    /// one is playing somewhere else, where none of its object ids mean
    /// anything here, so its casts are skipped entirely.
    /// </param>
    /// <param name="ownPlayerObjectId">
    /// This client's own character, so a cast attributed to it is never read
    /// back as somebody else's.
    /// </param>
    public IReadOnlyList<PluginPeerCast> CaptureRemoteCasts(
        long afterSequence,
        string worldName,
        uint ownPlayerObjectId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            TakeInRemoteCasts(now, worldName ?? string.Empty, ownPlayerObjectId);
            ForgetStaleObservations(now);
            var result = new List<PluginPeerCast>();
            foreach (ObservedCast observed in _observedCasts)
            {
                if (observed.Sequence > afterSequence)
                    result.Add(observed.Project(now));
            }
            return result.Count == 0
                ? Array.Empty<PluginPeerCast>()
                : result.ToArray();
        }
    }

    public void Withdraw()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            // Nothing to write the ring into any more; a later publish will
            // carry whatever is still young enough to matter.
            _castWritePending = false;
        }
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Withdraw();
        _disposed = true;
    }

    /// <summary>
    /// Every other client's note that is present, recent and well shaped.
    /// One reader for both what a peer is and what it cast, so a note cannot
    /// be trusted for one and unchecked for the other.
    /// </summary>
    private List<PeerNote> ReadRemoteNotes(DateTimeOffset now)
    {
        var notes = new List<PeerNote>();
        if (!Directory.Exists(_directory))
            return notes;
        long newestAllowed = now.Subtract(StaleAfter).ToUnixTimeMilliseconds();
        foreach (string file in Directory.EnumerateFiles(
            _directory,
            "peer-*.json",
            SearchOption.TopDirectoryOnly))
        {
            if (file.Equals(_path, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var info = new FileInfo(file);
                if (info.Length is <= 0 or > MaximumDocumentBytes)
                    continue;
                PeerDocument? document = JsonSerializer.Deserialize<PeerDocument>(
                    File.ReadAllText(file),
                    JsonOptions);
                if (document is null
                    || document.InstanceId == _instanceId
                    || document.InstanceId == Guid.Empty
                    || document.UpdatedUnixMs < newestAllowed
                    || document.ClientId == 0u
                    || document.PlayerId == 0u
                    || string.IsNullOrWhiteSpace(document.Name)
                    || document.Name.Length > 128
                    || document.WorldName is null
                    || document.WorldName.Length > 128
                    || document.Tags is null
                    || document.Tags.Length > 128
                    || !double.IsFinite(document.EastWest)
                    || !double.IsFinite(document.NorthSouth)
                    || !double.IsFinite(document.Elevation)
                    || !float.IsFinite(document.Heading)
                    || document.Casts is null
                    || document.Casts.Length > CastRingCapacity
                    || !AreCastsWellFormed(document.Casts))
                {
                    continue;
                }
                notes.Add(new PeerNote(file, document));
            }
            catch (IOException)
            {
                // A peer can atomically replace or remove its own heartbeat
                // between enumeration and read. It will reappear next scan.
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (JsonException)
            {
            }
        }
        return notes;
    }

    /// <summary>
    /// Takes every cast this client has not seen before out of the peers'
    /// notes and into its own list, numbering them in the order it first read
    /// them. A cast is counted against the peer's high-water mark even when
    /// it is then dropped, so a note nobody can use is not re-examined on
    /// every read.
    /// </summary>
    private void TakeInRemoteCasts(
        DateTimeOffset now,
        string worldName,
        uint ownPlayerObjectId)
    {
        foreach (PeerNote note in ReadRemoteNotes(now)
            .OrderBy(static note => note.Document.ClientId)
            .ThenBy(static note => note.Document.InstanceId))
        {
            PeerDocument document = note.Document;
            // A peer logged in somewhere else shares nothing but a hard disk:
            // its object ids name other creatures entirely.
            if (!string.Equals(
                document.WorldName,
                worldName,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var cursor = new PeerCursorKey(note.Path, document.InstanceId);
            long highest =
                _observedPeers.TryGetValue(cursor, out ObservedPeer peer)
                    ? peer.HighestSequence
                    : 0L;
            foreach (PeerCastEntry entry in OldestFirst(document.Casts))
            {
                if (entry.Sequence <= highest)
                    continue;
                highest = entry.Sequence;
                if (!IsUsable(entry, now, ownPlayerObjectId))
                    continue;
                _observedCasts.Add(new ObservedCast(
                    ++_observedSequence,
                    document.ClientId,
                    entry));
            }
            _observedPeers[cursor] = new ObservedPeer(highest, now);
        }
        if (_observedCasts.Count > ObservedCastCapacity)
        {
            _observedCasts.RemoveRange(
                0,
                _observedCasts.Count - ObservedCastCapacity);
        }
    }

    /// <summary>
    /// Whether every cast in a note is one this client could have written
    /// itself. Applied where the note is accepted, before anything walks it:
    /// a JSON null element and a stamp that is not a time both throw in that
    /// walk, and the throw would leave through the plugin's capture call.
    ///
    /// <para>One bad entry refuses the whole note. The same rule runs before
    /// an entry reaches this client's own ring, so a note carrying one was
    /// not written by an honest client and the rest of it is worth no more
    /// than the bad row.</para>
    /// </summary>
    private static bool AreCastsWellFormed(PeerCastEntry?[] casts)
    {
        foreach (PeerCastEntry? entry in casts)
        {
            if (entry is null || !IsWellFormed(entry))
                return false;
        }
        return true;
    }

    /// <summary>
    /// A note's casts, oldest first. Null elements cannot reach here: a note
    /// carrying one was refused whole when it was read.
    /// </summary>
    private static IEnumerable<PeerCastEntry> OldestFirst(PeerCastEntry?[] casts) =>
        casts.OfType<PeerCastEntry>().OrderBy(static entry => entry.Sequence);

    /// <summary>
    /// Whether a cast makes sense at all. The one rule, applied on the way
    /// into this client's own ring and again on the way out of a peer's
    /// note, so a note this client would refuse to read is a note it never
    /// writes either.
    /// </summary>
    private static bool IsWellFormed(PeerCastEntry entry) =>
        entry.Sequence > 0L
        && entry.Sequence <= MaximumCastSequence
        && entry.AtUnixMs >= EarliestCastUnixMs
        && entry.AtUnixMs <= LatestCastUnixMs
        && entry.CasterObjectId != 0u
        && entry.TargetObjectId != 0u
        && entry.SpellId != 0u
        && entry.EffectiveSkill >= 0
        && double.IsFinite(entry.DurationSeconds)
        && entry.DurationSeconds >= 0d
        && entry.DurationSeconds <= MaximumCastDurationSeconds
        // An attempt carries no duration; something that landed must.
        && (!entry.Landed || entry.DurationSeconds > 0d);

    /// <summary>
    /// Whether one entry in a peer's note is a cast this client can act on:
    /// well formed, recent, and not this client's own doing. Whether the
    /// spell itself means anything is settled against this client's own
    /// spell table, by the caller that has one.
    /// </summary>
    private static bool IsUsable(
        PeerCastEntry entry,
        DateTimeOffset now,
        uint ownPlayerObjectId)
    {
        // The well-formed rule comes first and the age is only measured
        // after it: a stamp this rule refuses is one the measurement would
        // throw on.
        if (!IsWellFormed(entry) || entry.CasterObjectId == ownPlayerObjectId)
            return false;
        TimeSpan age = Age(entry, now);
        // Too old to act on, or stamped in the future by a note whose clock
        // cannot be trusted.
        return age <= StaleAfter && age >= -StaleAfter;
    }

    private static TimeSpan Age(PeerCastEntry entry, DateTimeOffset now) =>
        now - DateTimeOffset.FromUnixTimeMilliseconds(entry.AtUnixMs);

    /// <summary>
    /// Drops casts that have aged out of the staleness window and peers that
    /// have not been heard from for several windows, so neither list grows
    /// with the length of the session.
    /// </summary>
    private void ForgetStaleObservations(DateTimeOffset now)
    {
        _observedCasts.RemoveAll(observed => Age(observed.Entry, now) > StaleAfter);
        TimeSpan forgetPeerAfter = StaleAfter * 4;
        foreach (PeerCursorKey cursor in _observedPeers
            .Where(pair => now - pair.Value.LastSeen > forgetPeerAfter)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            _observedPeers.Remove(cursor);
        }
    }

    /// <summary>A peer's note: the file it was read from, and what it says.</summary>
    private readonly record struct PeerNote(string Path, PeerDocument Document);

    /// <summary>
    /// What a high-water mark belongs to. The identity in a note is a field
    /// another process wrote, so two files can claim one identity; keyed on
    /// the claim alone, whichever file was read first moved the mark the
    /// other's casts are measured against, and the genuine client was
    /// silenced for good. Keyed on the file as well, a note can only ever
    /// move its own file's mark.
    ///
    /// <para>The path is compared exactly as the directory scan reports it,
    /// which is the same spelling every scan, and is the right comparison on
    /// a file system where two names differing only in case are two
    /// files.</para>
    /// </summary>
    private readonly record struct PeerCursorKey(string Path, Guid InstanceId);

    /// <summary>How far one peer's casts have been read.</summary>
    private readonly record struct ObservedPeer(
        long HighestSequence,
        DateTimeOffset LastSeen);

    /// <summary>One cast read from a peer, under this client's numbering.</summary>
    private readonly record struct ObservedCast(
        long Sequence,
        uint ClientId,
        PeerCastEntry Entry)
    {
        /// <summary>
        /// What the plugin is handed: the time left rather than the total, so
        /// a success read five seconds after it happened is not applied as if
        /// it were fresh.
        /// </summary>
        internal PluginPeerCast Project(DateTimeOffset now)
        {
            double elapsed = Math.Max(0d, Age(Entry, now).TotalSeconds);
            return new PluginPeerCast(
                Sequence,
                ClientId,
                Entry.CasterObjectId,
                Entry.TargetObjectId,
                Entry.SpellId,
                Entry.EffectiveSkill,
                Math.Clamp(
                    Entry.DurationSeconds - elapsed,
                    0d,
                    Entry.DurationSeconds),
                Entry.Landed);
        }
    }

    /// <summary>One cast as it travels in the document.</summary>
    private sealed class PeerCastEntry
    {
        public long Sequence { get; set; }
        public long AtUnixMs { get; set; }
        public uint CasterObjectId { get; set; }
        public uint TargetObjectId { get; set; }
        public uint SpellId { get; set; }
        public int EffectiveSkill { get; set; }
        public double DurationSeconds { get; set; }
        public bool Landed { get; set; }
    }

    private sealed class PeerDocument
    {
        public Guid InstanceId { get; set; }
        public long UpdatedUnixMs { get; set; }
        public uint ClientId { get; set; }
        public uint PlayerId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string WorldName { get; set; } = string.Empty;
        public string[] Tags { get; set; } = [];
        public uint CellId { get; set; }
        public double EastWest { get; set; }
        public double NorthSouth { get; set; }
        public double Elevation { get; set; }
        public bool IsOutdoor { get; set; }
        public float Heading { get; set; }
        public uint CurrentHealth { get; set; }
        public uint CurrentMana { get; set; }
        public uint CurrentStamina { get; set; }
        public uint MaxHealth { get; set; }
        public uint MaxMana { get; set; }
        public uint MaxStamina { get; set; }

        /// <summary>
        /// The recent casts this client has announced, oldest first. A
        /// heartbeat carries them again unchanged; a reader tells old from
        /// new by the sequence.
        ///
        /// <para>Nullable because the wire is a file another process wrote:
        /// a JSON null in the array lands here as a null element, and the
        /// type has to say so or the check for one reads as dead code.</para>
        /// </summary>
        public PeerCastEntry?[] Casts { get; set; } = [];

        public static PeerDocument From(
            in PluginNetworkClient client,
            Guid instanceId,
            long updatedUnixMs,
            PeerCastEntry[] casts) => new()
        {
            InstanceId = instanceId,
            UpdatedUnixMs = updatedUnixMs,
            ClientId = client.ClientId,
            PlayerId = client.PlayerId,
            Name = client.Name,
            WorldName = client.WorldName,
            Tags = client.Tags
                .Where(static tag => !string.IsNullOrWhiteSpace(tag))
                .Select(static tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(128)
                .ToArray(),
            CellId = client.Position.CellId,
            EastWest = client.Position.EastWest,
            NorthSouth = client.Position.NorthSouth,
            Elevation = client.Position.Elevation,
            IsOutdoor = client.Position.IsOutdoor,
            Heading = client.Heading,
            CurrentHealth = client.CurrentHealth,
            CurrentMana = client.CurrentMana,
            CurrentStamina = client.CurrentStamina,
            MaxHealth = client.MaxHealth,
            MaxMana = client.MaxMana,
            MaxStamina = client.MaxStamina,
            Casts = casts,
        };

        public PluginNetworkClient ToClient() => new(
            ClientId,
            PlayerId,
            Name,
            WorldName,
            new PluginNavigationPosition(
                CellId,
                EastWest,
                NorthSouth,
                Elevation,
                Heading,
                IsOutdoor),
            Tags,
            CurrentHealth,
            CurrentMana,
            CurrentStamina,
            MaxHealth,
            MaxMana,
            MaxStamina,
            Heading);
    }
}
