using System.Text.Json.Nodes;
using AcDream.Runtime.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Tests.Plugins;

public sealed class LocalPluginPeerRegistryTests
{
    [Fact]
    public void PublishesRemoteClientsIgnoresSelfAndExpiresStaleHeartbeat()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"acdream-plugin-peers-{Guid.NewGuid():N}");
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var first = new LocalPluginPeerRegistry(
                root,
                time,
                Guid.Parse("11111111-1111-1111-1111-111111111111"));
            using var second = new LocalPluginPeerRegistry(
                root,
                time,
                Guid.Parse("22222222-2222-2222-2222-222222222222"));
            first.Publish(Client(first.ClientId, 10u, "Alpha", ["one"]));
            second.Publish(Client(second.ClientId, 20u, "Beta", ["two"]));

            PluginNetworkClient remote = Assert.Single(
                first.CaptureRemoteClients());
            Assert.Equal(second.ClientId, remote.ClientId);
            Assert.Equal("Beta", remote.Name);
            Assert.Equal(["two"], remote.Tags);
            Assert.Equal(33.5d, remote.Position.EastWest);

            time.Advance(LocalPluginPeerRegistry.StaleAfter
                + TimeSpan.FromMilliseconds(1));
            Assert.Empty(first.CaptureRemoteClients());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The point of the ring: a cast is an event on a transport that only
    /// carries state. One client casts, the other reads it once, moves its
    /// cursor on and is not handed it again -- and never sees a cast put
    /// under its own character's name.
    ///
    /// Mutation checks (2026-09-21):
    /// * dropping the per-peer high-water mark (taking every ring entry in on
    ///   every read) turned this red with "two casts after the cursor moved";
    /// * dropping the own-caster rule let the planted cast through and the
    ///   count went to two.
    /// </summary>
    [Fact]
    public void ACastCrossesToTheOtherClientOnceAndNeverComesBackAsItsOwn()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var caster = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            caster.Publish(Client(caster.ClientId, 10u, "Alpha", ["one"]));
            reader.Publish(Client(reader.ClientId, 20u, "Beta", ["two"]));

            caster.RecordCast(new LocalPluginCast(
                CasterObjectId: 10u,
                TargetObjectId: 0x50000012u,
                SpellId: 42u,
                EffectiveSkill: 357,
                DurationSeconds: 60d,
                Landed: true));
            // A note that claims the READER cast something. Nothing stops a
            // client writing that; the reader has to refuse to believe it.
            caster.RecordCast(new LocalPluginCast(
                CasterObjectId: 20u,
                TargetObjectId: 0x50000012u,
                SpellId: 43u,
                EffectiveSkill: 357,
                DurationSeconds: 60d,
                Landed: true));
            time.Advance(LocalPluginPeerRegistry.CastWriteDebounce);
            caster.Publish(Client(caster.ClientId, 10u, "Alpha", ["one"]));

            PluginPeerCast only = Assert.Single(
                reader.CaptureRemoteCasts(0L, "Coldeve", 20u));
            Assert.Equal(caster.ClientId, only.ClientId);
            Assert.Equal(10u, only.CasterObjectId);
            Assert.Equal(0x50000012u, only.TargetObjectId);
            Assert.Equal(42u, only.SpellId);
            Assert.Equal(357, only.EffectiveSkill);
            Assert.True(only.Landed);
            // Handed the time left, counted down from when the caster said it
            // happened, never the total it published.
            Assert.Equal(
                60d - LocalPluginPeerRegistry.CastWriteDebounce.TotalSeconds,
                only.SecondsRemaining,
                3);

            Assert.Empty(reader.CaptureRemoteCasts(
                only.Sequence, "Coldeve", 20u));
            // Reading from the start again hands back the one cast, not two:
            // a heartbeat republishing the same ring is not a new event.
            Assert.Single(reader.CaptureRemoteCasts(0L, "Coldeve", 20u));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A cast ages out of the window even while the client that cast it keeps
    /// heartbeating, so a plugin joining late never acts on a debuff that
    /// lapsed minutes ago.
    ///
    /// Mutation check (2026-09-21): removing the age rule from the pruning
    /// pass turned this red -- the twenty-second-old cast was still handed
    /// back.
    /// </summary>
    [Fact]
    public void ACastAgesOutOfTheWindowEvenWhileItsClientKeepsHeartbeating()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var caster = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            caster.RecordCast(Landed(10u, 0x50000012u, 42u));
            caster.Publish(Client(caster.ClientId, 10u, "Alpha", []));
            Assert.Single(reader.CaptureRemoteCasts(0L, "Coldeve", 20u));

            // Still inside the window, and the peer is still here.
            time.Advance(TimeSpan.FromSeconds(10));
            caster.Publish(Client(caster.ClientId, 10u, "Alpha", []));
            Assert.Single(reader.CaptureRemoteCasts(0L, "Coldeve", 20u));

            time.Advance(TimeSpan.FromSeconds(10));
            caster.Publish(Client(caster.ClientId, 10u, "Alpha", []));
            Assert.Empty(reader.CaptureRemoteCasts(0L, "Coldeve", 20u));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A client logged in to another world shares a hard disk with this one
    /// and nothing else: its object ids name other creatures. Its casts are
    /// skipped although its note is perfectly well formed, which is why the
    /// client list still shows it.
    ///
    /// Mutation check (2026-09-21): removing the world comparison turned the
    /// first assertion red with one cast instead of none.
    /// </summary>
    [Fact]
    public void ACastFromAClientInAnotherWorldIsSkipped()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var caster = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            caster.RecordCast(Landed(10u, 0x50000012u, 42u));
            caster.Publish(Client(caster.ClientId, 10u, "Alpha", []));

            Assert.Empty(reader.CaptureRemoteCasts(0L, "Frostfell", 20u));
            Assert.Single(reader.CaptureRemoteClients());
            Assert.Single(reader.CaptureRemoteCasts(0L, "Coldeve", 20u));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The ring's two jobs: it holds a burst that a single-field document
    /// would lose, and it is capped so the note cannot grow without bound.
    /// Forty casts leave the last thirty-two.
    ///
    /// Mutation check (2026-09-21): removing the cap turned this red with
    /// forty casts and a first sequence of one.
    /// </summary>
    [Fact]
    public void TheRingKeepsTheMostRecentCastsAndNoMore()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var caster = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            for (uint index = 1; index <= 40u; index++)
                caster.RecordCast(Landed(10u, 0x50000000u + index, 40u + index));
            caster.Publish(Client(caster.ClientId, 10u, "Alpha", []));

            IReadOnlyList<PluginPeerCast> read =
                reader.CaptureRemoteCasts(0L, "Coldeve", 20u);
            Assert.Equal(
                LocalPluginPeerRegistry.CastRingCapacity,
                read.Count);
            // The oldest eight fell off the front, so the first one left is
            // the ninth that was cast.
            Assert.Equal(0x50000009u, read[0].TargetObjectId);
            Assert.Equal(0x50000028u, read[^1].TargetObjectId);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The debounce: a burst of casts costs one write, not one write each.
    /// The note is a whole-document replace, so writing it per cast would
    /// rewrite several kilobytes six times inside a debuff chain.
    ///
    /// Mutation check (2026-09-21): making the write always due turned the
    /// two "not due yet" assertions red.
    /// </summary>
    [Fact]
    public void ABurstOfCastsAsksForOneWriteRatherThanOneEach()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var caster = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            caster.Publish(Client(caster.ClientId, 10u, "Alpha", []));
            Assert.False(caster.IsCastWriteDue());

            caster.RecordCast(Landed(10u, 0x50000012u, 42u));
            Assert.False(caster.IsCastWriteDue());
            time.Advance(TimeSpan.FromMilliseconds(50));
            caster.RecordCast(Landed(10u, 0x50000013u, 43u));
            Assert.False(caster.IsCastWriteDue());

            time.Advance(LocalPluginPeerRegistry.CastWriteDebounce);
            Assert.True(caster.IsCastWriteDue());
            caster.Publish(Client(caster.ClientId, 10u, "Alpha", []));
            Assert.False(caster.IsCastWriteDue());

            // One write, both casts.
            Assert.Equal(
                2,
                reader.CaptureRemoteCasts(0L, "Coldeve", 20u).Count);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// What a note may claim about a cast. One rule, applied on the way into
    /// the ring and again on the way out of a peer's note, so a cast this
    /// client would refuse to read is one it never writes either. A duration
    /// that is not a finite number is the sharp case: it cannot be written as
    /// JSON at all, so letting one into the ring would make every later write
    /// of the whole note throw and take this client's announcements with it.
    ///
    /// Mutation check (2026-09-21): dropping any one clause from the
    /// well-formed rule let its row through and turned this red; before the
    /// rule existed the two non-finite rows failed inside the writer with
    /// "positive and negative infinity cannot be written as valid JSON".
    /// </summary>
    [Theory]
    // No target: nothing to attribute the effect to.
    [InlineData(0u, 42u, 60d, true, 0)]
    // No spell.
    [InlineData(0x50000012u, 0u, 60d, true, 0)]
    // A duration that is not a number at all.
    [InlineData(0x50000012u, 42u, double.NaN, true, 0)]
    [InlineData(0x50000012u, 42u, double.PositiveInfinity, true, 0)]
    // A landed cast that claims no duration, and one that claims a week.
    [InlineData(0x50000012u, 42u, 0d, true, 0)]
    [InlineData(0x50000012u, 42u, 604800d, true, 0)]
    // An attempt legitimately carries no duration.
    [InlineData(0x50000012u, 42u, 0d, false, 1)]
    [InlineData(0x50000012u, 42u, 60d, true, 1)]
    public void ANoteIsBelievedOnlyWhereItsCastMakesSense(
        uint targetObjectId,
        uint spellId,
        double durationSeconds,
        bool landed,
        int expected)
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var caster = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            bool recorded = caster.RecordCast(new LocalPluginCast(
                10u, targetObjectId, spellId, 357, durationSeconds, landed));
            caster.Publish(Client(caster.ClientId, 10u, "Alpha", []));

            Assert.Equal(expected == 1, recorded);
            Assert.Equal(
                expected,
                reader.CaptureRemoteCasts(0L, "Coldeve", 20u).Count);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A note is written by another process running as this user, so it is
    /// hostile input: nothing in it may come back out of a plugin call as an
    /// exception. These two shapes did. A JSON null element was dereferenced
    /// by the walk that sorts the casts, and a stamp outside the range a date
    /// can represent threw inside the conversion that ages a cast -- both
    /// straight through the capture call and into the plugin.
    ///
    /// The honest writer cannot produce either, which is why every test that
    /// went through it missed them; these write the bytes themselves.
    ///
    /// Mutation check (2026-09-21): with the per-entry check taken back out
    /// of the point where a note is accepted, the null row threw
    /// NullReferenceException and both stamp rows threw
    /// ArgumentOutOfRangeException out of CaptureRemoteCasts.
    /// </summary>
    [Theory]
    [InlineData("null-element")]
    [InlineData("stamp-at-long-max")]
    [InlineData("stamp-at-long-min")]
    public void ANoteNoHonestWriterCouldProduceIsRefusedRatherThanThrown(
        string shape)
    {
        string root = TemporaryRoot();
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        try
        {
            using var reader = Registry(root, time, 2);
            JsonObject note = RawNote(now, HostileInstance);
            note["Casts"] = new JsonArray(shape switch
            {
                "null-element" => null,
                "stamp-at-long-max" => RawCast(1L, long.MaxValue),
                _ => RawCast(1L, long.MinValue),
            });
            WriteRawNote(root, HostileInstance, note);
            // A second, honest peer in the same folder: the reader has to
            // survive the hostile note, not merely not crash on an empty one.
            using var honest = Registry(root, time, 1);
            honest.RecordCast(Landed(10u, 0x50000012u, 42u));
            honest.Publish(Client(honest.ClientId, 10u, "Alpha", []));

            IReadOnlyList<PluginPeerCast> read =
                reader.CaptureRemoteCasts(0L, "Coldeve", 20u);

            PluginPeerCast only = Assert.Single(read);
            Assert.Equal(honest.ClientId, only.ClientId);
            Assert.Equal(42u, only.SpellId);
            // The hostile note names a client too, and its note is refused
            // for the client list on the same rule.
            PluginNetworkClient client = Assert.Single(
                reader.CaptureRemoteClients());
            Assert.Equal(honest.ClientId, client.ClientId);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// One bad entry refuses the whole note rather than being skipped. The
    /// writer applies the same rule before an entry ever reaches the ring, so
    /// a note carrying one was not written by an honest client and nothing
    /// else in it is worth believing either.
    /// </summary>
    [Fact]
    public void ANoteWithOneImpossibleCastIsRefusedWhole()
    {
        string root = TemporaryRoot();
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        try
        {
            using var reader = Registry(root, time, 2);
            JsonObject note = RawNote(now, HostileInstance);
            note["Casts"] = new JsonArray(
                RawCast(1L, now.ToUnixTimeMilliseconds()),
                RawCast(2L, now.ToUnixTimeMilliseconds(), spellId: 0u),
                RawCast(3L, now.ToUnixTimeMilliseconds()));
            WriteRawNote(root, HostileInstance, note);

            Assert.Empty(reader.CaptureRemoteCasts(0L, "Coldeve", 20u));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>The identity every raw note in these tests claims.</summary>
    private static readonly Guid HostileInstance =
        Guid.Parse("99999999-9999-9999-9999-999999999999");

    /// <summary>
    /// A peer note as another process on this computer would write it: the
    /// bytes are composed here rather than by this client's own writer, so a
    /// test can put things in the file that the writer would never produce.
    /// Every field carries the value an honest note would, so a test changes
    /// only the one thing it is about.
    /// </summary>
    private static JsonObject RawNote(DateTimeOffset now, Guid instanceId) =>
        new()
        {
            ["InstanceId"] = instanceId.ToString(),
            ["UpdatedUnixMs"] = now.ToUnixTimeMilliseconds(),
            ["ClientId"] = 7u,
            ["PlayerId"] = 10u,
            ["Name"] = "Alpha",
            ["WorldName"] = "Coldeve",
            ["Tags"] = new JsonArray(),
            ["CellId"] = 0x7F7F0001u,
            ["EastWest"] = 33.5d,
            ["NorthSouth"] = -72.8d,
            ["Elevation"] = 1d,
            ["IsOutdoor"] = true,
            ["Heading"] = 90f,
            ["CurrentHealth"] = 90u,
            ["CurrentMana"] = 70u,
            ["CurrentStamina"] = 80u,
            ["MaxHealth"] = 100u,
            ["MaxMana"] = 100u,
            ["MaxStamina"] = 100u,
            ["Casts"] = new JsonArray(),
        };

    private static JsonObject RawCast(
        long sequence,
        long atUnixMs,
        uint casterObjectId = 10u,
        uint targetObjectId = 0x50000012u,
        uint spellId = 42u) => new()
        {
            ["Sequence"] = sequence,
            ["AtUnixMs"] = atUnixMs,
            ["CasterObjectId"] = casterObjectId,
            ["TargetObjectId"] = targetObjectId,
            ["SpellId"] = spellId,
            ["EffectiveSkill"] = 357,
            ["DurationSeconds"] = 60d,
            ["Landed"] = true,
        };

    private static void WriteRawNote(
        string root,
        Guid instanceId,
        JsonNode note) =>
        WriteRawNote(root, instanceId, note.ToJsonString());

    /// <summary>
    /// Puts bytes where a peer's note lives. The name is the one the reader
    /// scans for; nothing else about the file went through this client.
    /// </summary>
    private static void WriteRawNote(string root, Guid instanceId, string json)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(
            Path.Combine(root, $"peer-{instanceId:N}.json"),
            json);
    }

    private static LocalPluginCast Landed(
        uint casterObjectId,
        uint targetObjectId,
        uint spellId) => new(
            casterObjectId, targetObjectId, spellId, 357, 60d, Landed: true);

    private static LocalPluginPeerRegistry Registry(
        string root,
        TimeProvider time,
        int which) => new(
            root,
            time,
            Guid.Parse($"{which:D8}-0000-0000-0000-000000000000"));

    private static string TemporaryRoot() => Path.Combine(
        Path.GetTempPath(),
        $"acdream-plugin-peers-{Guid.NewGuid():N}");

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static PluginNetworkClient Client(
        uint clientId,
        uint playerId,
        string name,
        IReadOnlyList<string> tags) => new(
            clientId,
            playerId,
            name,
            "Coldeve",
            new PluginNavigationPosition(
                0x7F7F0001u, 33.5d, -72.8d, 1d, 90f, true),
            tags,
            90u,
            70u,
            80u,
            100u,
            100u,
            100u,
            90f);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
