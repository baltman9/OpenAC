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
    /// NONE of the forty read back, not forty. A note carrying more entries
    /// than the ring holds is refused whole by the reader's length guard, so
    /// a writer that let its ring grow would not be heard at all.
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

    /// <summary>
    /// The cursor that says how far a peer has been read is tied to the file
    /// the note came from as well as to the identity the note claims. The
    /// identity is a field another process wrote, so two files can claim one:
    /// keyed on the claim alone, whichever was read first moved the mark the
    /// other's casts are measured against, and the genuine client's casts were
    /// dropped for good.
    ///
    /// Mutation check (2026-09-21): with the cursor keyed on the claimed
    /// identity alone, the genuine client's three casts never arrived -- zero
    /// instead of three.
    /// </summary>
    [Fact]
    public void AFileClaimingAPeersIdentityCannotSilenceThatPeer()
    {
        string root = TemporaryRoot();
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        try
        {
            using var reader = Registry(root, time, 2);
            using var genuine = Registry(root, time, 1);

            // A second file, under a name of its own, claiming the genuine
            // client's identity and a sequence past anything it has sent.
            JsonObject impostor = RawNote(now, Instance(1));
            impostor["Casts"] = new JsonArray(
                RawCast(9L, now.ToUnixTimeMilliseconds(), spellId: 99u));
            WriteRawNote(root, HostileInstance, impostor);
            PluginPeerCast claimed = Assert.Single(
                reader.CaptureRemoteCasts(0L, "Coldeve", 20u));

            for (uint index = 1; index <= 3u; index++)
                genuine.RecordCast(Landed(10u, 0x50000000u + index, 40u + index));
            time.Advance(LocalPluginPeerRegistry.CastWriteDebounce);
            genuine.Publish(Client(genuine.ClientId, 10u, "Alpha", []));

            Assert.Equal(
                3,
                reader.CaptureRemoteCasts(
                    claimed.Sequence, "Coldeve", 20u).Count);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A sequence is a number in a file and the reader's cursor for a peer is
    /// set from it, so a note claiming a number no client could count to would
    /// park that cursor past everything the genuine client will ever send --
    /// for the rest of the session, because the genuine client's own
    /// heartbeats keep it from being forgotten. A process running as this user
    /// can write any file, including the genuine client's own, where tying the
    /// cursor to the file does not help. So the number itself is bounded.
    ///
    /// Mutation check (2026-09-21): without the bound the poisoned note was
    /// believed (one cast instead of none) and the genuine client's two casts
    /// never came back afterwards -- zero instead of two.
    /// </summary>
    [Fact]
    public void ASequenceNoClientCouldCountToIsRefusedRatherThanBelieved()
    {
        string root = TemporaryRoot();
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        try
        {
            using var reader = Registry(root, time, 2);
            using var genuine = Registry(root, time, 1);
            genuine.RecordCast(Landed(10u, 0x50000012u, 42u));
            genuine.Publish(Client(genuine.ClientId, 10u, "Alpha", []));

            // Written over the genuine client's own note, in its own file.
            JsonObject poisoned = RawNote(now, Instance(1));
            poisoned["ClientId"] = genuine.ClientId;
            poisoned["Casts"] = new JsonArray(RawCast(
                long.MaxValue,
                now.ToUnixTimeMilliseconds(),
                spellId: 99u));
            WriteRawNote(root, Instance(1), poisoned);
            Assert.Empty(reader.CaptureRemoteCasts(0L, "Coldeve", 20u));

            // The genuine client heartbeats again, carrying its whole ring.
            time.Advance(LocalPluginPeerRegistry.CastWriteDebounce);
            genuine.RecordCast(Landed(10u, 0x50000013u, 43u));
            genuine.Publish(Client(genuine.ClientId, 10u, "Alpha", []));

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
    /// A position that is not a finite number of metres cannot be written as
    /// JSON at all -- the writer refuses not-a-number and infinity outright --
    /// so copying one into the note made the write throw, and the publish path
    /// catches only file errors, so it left through the plugin tick. A reader
    /// refuses such a note anyway, so the note is refused here instead.
    ///
    /// Mutation check (2026-09-21): without the check on the way in, every row
    /// threw ArgumentException out of the write -- ".NET number values such as
    /// positive and negative infinity cannot be written as valid JSON".
    /// </summary>
    [Theory]
    [InlineData(double.NaN, 0d, 0d, 0f)]
    [InlineData(0d, double.PositiveInfinity, 0d, 0f)]
    [InlineData(0d, 0d, double.NegativeInfinity, 0f)]
    [InlineData(0d, 0d, 0d, float.NaN)]
    public void APositionThatIsNotANumberIsRefusedRatherThanThrownAtTheCaller(
        double eastWest,
        double northSouth,
        double elevation,
        float heading)
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var caster = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);

            Assert.Null(Record.Exception(() => caster.Publish(ClientAt(
                caster.ClientId, eastWest, northSouth, elevation, heading))));

            // Nothing was written, which is what a reader would have made of
            // such a note in any case.
            Assert.Empty(reader.CaptureRemoteClients());
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A write this client could not do must not become a per-frame retry. The
    /// pending flag and the last-write stamp were only touched after a write
    /// that worked, so a failure left a write permanently due -- and the
    /// hosts' tick skips its early-out whenever a write is due, so a five
    /// second heartbeat turned into an attempt every frame, silently for a
    /// file error.
    ///
    /// Mutation check (2026-09-21): with the hold-off taken out, the write was
    /// still due immediately after the attempt on both rows.
    /// </summary>
    [Theory]
    // The client's own state cannot be written as JSON.
    [InlineData("refused")]
    // The file cannot be written: something else holds the directory's name.
    [InlineData("failed")]
    public void AWriteThatCouldNotBeDoneWaitsTheHeartbeatBeforeTheNextTry(
        string how)
    {
        bool refused = how == "refused";
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        if (!refused)
            File.WriteAllText(root, "not a directory");
        try
        {
            using var caster = Registry(root, time, 1);
            caster.RecordCast(Landed(10u, 0x50000012u, 42u));
            Assert.True(caster.IsCastWriteDue());

            PluginNetworkClient client = refused
                ? ClientAt(caster.ClientId, double.NaN, 0d, 0d, 0f)
                : Client(caster.ClientId, 10u, "Alpha", []);
            if (refused)
                Assert.Null(Record.Exception(() => caster.Publish(client)));
            else
                Assert.Throws<IOException>(() => caster.Publish(client));

            Assert.False(caster.IsCastWriteDue());
            time.Advance(LocalPluginPeerRegistry.CastWriteDebounce);
            Assert.False(caster.IsCastWriteDue());

            // One attempt per heartbeat, which is what the note costs anyway.
            time.Advance(LocalPluginPeerRegistry.HeartbeatPeriod);
            Assert.True(caster.IsCastWriteDue());
        }
        finally
        {
            Delete(root);
            if (File.Exists(root))
                File.Delete(root);
        }
    }

    /// <summary>
    /// Bytes that are not a note this client would ever write: a field left
    /// out, a field of the wrong type, a file cut off mid-write. None of them
    /// may cost the reader anything -- it takes what it can use and carries
    /// on, and the honest peer beside them is still read.
    /// </summary>
    [Theory]
    // A field the reader needs, left out entirely.
    [InlineData("{\"InstanceId\":\"99999999-9999-9999-9999-999999999999\"}")]
    // The right field, the wrong type.
    [InlineData("{\"InstanceId\":\"99999999-9999-9999-9999-999999999999\","
        + "\"UpdatedUnixMs\":\"very recently\",\"ClientId\":7,\"PlayerId\":10,"
        + "\"Name\":\"Alpha\",\"WorldName\":\"Coldeve\",\"Casts\":[]}")]
    // Caught mid-write by another process, or by the power going out.
    [InlineData("{\"InstanceId\":\"99999999-9999-9999-9999-9999")]
    // Not JSON at all.
    [InlineData("hello")]
    public void BytesThatAreNotANoteCostTheReaderNothing(string json)
    {
        string root = TemporaryRoot();
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        try
        {
            using var reader = Registry(root, time, 2);
            WriteRawNote(root, HostileInstance, json);
            using var honest = Registry(root, time, 1);
            honest.RecordCast(Landed(10u, 0x50000012u, 42u));
            honest.Publish(Client(honest.ClientId, 10u, "Alpha", []));

            Assert.Single(reader.CaptureRemoteCasts(0L, "Coldeve", 20u));
            Assert.Single(reader.CaptureRemoteClients());
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The two clauses of the well-formed rule that this client's own writer
    /// can never break, so nothing going through it can exercise them: it
    /// numbers its own casts from one upwards, and it settles the caster
    /// before it records anything. A file another process wrote is under no
    /// such discipline. Each note here carries a second, unremarkable cast
    /// as well, because what the clause decides is whether the note is
    /// believed at all -- a cast numbered zero or below would be skipped by
    /// the cursor anyway, but the note it came in is not one an honest client
    /// wrote, and nothing else in it is taken either.
    ///
    /// Mutation check (2026-09-21): deleting the positive-sequence clause
    /// turned the first two rows red and deleting the non-zero-caster clause
    /// turned the third red -- each let the note's companion cast through.
    /// </summary>
    [Theory]
    [InlineData(0L, 10u)]
    [InlineData(-1L, 10u)]
    [InlineData(1L, 0u)]
    public void ANoteClaimingACastWithNoNumberOrNoCasterIsNotBelievedAtAll(
        long sequence,
        uint casterObjectId)
    {
        string root = TemporaryRoot();
        var now = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTimeProvider(now);
        try
        {
            using var reader = Registry(root, time, 2);
            JsonObject note = RawNote(now, HostileInstance);
            note["Casts"] = new JsonArray(
                RawCast(sequence, now.ToUnixTimeMilliseconds(), casterObjectId),
                RawCast(5L, now.ToUnixTimeMilliseconds(), spellId: 43u));
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

    /// <summary>
    /// The point of the command ring: one client asks the others on this
    /// machine to run a line, they read it once, and a note that claims the
    /// READER asked for something is not believed. Nothing stops a client
    /// writing that; the reader has to refuse it.
    ///
    /// Mutation checks (2026-09-22):
    /// * dropping the per-peer command high-water mark (taking every ring
    ///   entry in on every read) turned this red with two lines after the
    ///   cursor moved;
    /// * dropping the own-sender rule let the planted line through and the
    ///   count went to two.
    /// </summary>
    [Fact]
    public void ACommandLineCrossesToTheOtherClientOnceAndNeverComesBackAsItsOwn()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var sender = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));
            reader.Publish(Client(reader.ClientId, 20u, "Beta", []));

            Assert.True(sender.RecordCommand(
                new LocalPluginCommand(10u, [], "/example go", 0)));
            // A note that claims the READER asked for a line.
            Assert.True(sender.RecordCommand(
                new LocalPluginCommand(20u, [], "/example stop", 0)));
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));

            LocalPluginPeerCommand only = Assert.Single(
                reader.CaptureRemoteCommands(0L, "Coldeve", 20u, []));
            Assert.Equal(sender.ClientId, only.Command.ClientId);
            Assert.Equal(10u, only.Command.SenderObjectId);
            Assert.Equal("/example go", only.Command.Line);
            Assert.Empty(only.Command.Tags);
            Assert.Equal(0, only.StaggerMilliseconds);
            Assert.Equal(time.GetUtcNow(), only.Command.SentAt);

            Assert.Empty(reader.CaptureRemoteCommands(
                only.Command.Sequence, "Coldeve", 20u, []));
            // Reading from the start again hands back the one line, not two:
            // a heartbeat republishing the same ring is not a new ask.
            Assert.Single(reader.CaptureRemoteCommands(0L, "Coldeve", 20u, []));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// Labels are how a broadcast picks its audience: a line aimed at labels
    /// reaches a client wearing one of them and nobody else, and a line
    /// aimed at none reaches everybody.
    ///
    /// Mutation check (2026-09-22): treating an aimed line as if it were
    /// aimed at nobody in particular -- always matching -- turned the
    /// "tank" row red.
    /// </summary>
    [Theory]
    [InlineData(new string[0], new[] { "healer" }, true)]
    [InlineData(new[] { "healer" }, new[] { "healer" }, true)]
    [InlineData(new[] { "HEALER" }, new[] { "healer" }, true)]
    [InlineData(new[] { "healer", "tank" }, new[] { "tank" }, true)]
    [InlineData(new[] { "tank" }, new[] { "healer" }, false)]
    [InlineData(new[] { "tank" }, new string[0], false)]
    public void ALineOnlyReachesAClientWearingOneOfTheLabelsItIsAimedAt(
        string[] aimedAt,
        string[] readerTags,
        bool reaches)
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var sender = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            reader.Publish(Client(reader.ClientId, 20u, "Beta", readerTags));

            Assert.True(sender.RecordCommand(
                new LocalPluginCommand(10u, aimedAt, "/example go", 0)));
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));

            Assert.Equal(
                reaches ? 1 : 0,
                reader.CaptureRemoteCommands(0L, "Coldeve", 20u, readerTags)
                    .Count);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The stagger: the clients taking one line order themselves by client
    /// id with the sender first, and each waits its own place in that order
    /// times the delay the sender asked for. Three clients here, so the two
    /// recipients wait one delay and two delays -- and neither has to be
    /// told, because each works its own place out from the notes in the
    /// folder.
    ///
    /// Mutation checks (2026-09-22):
    /// * counting the sender as a recipient (dropping the +1) turned the
    ///   lower client's wait to zero;
    /// * counting every peer rather than only those ahead by client id gave
    ///   both clients the same wait.
    /// </summary>
    [Fact]
    public void TheRecipientsStaggerThemselvesByClientIdWithTheSenderFirst()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var sender = Registry(root, time, 1);
            using var first = Registry(root, time, 2);
            using var second = Registry(root, time, 3);
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));
            first.Publish(Client(first.ClientId, 20u, "Beta", []));
            second.Publish(Client(second.ClientId, 30u, "Gamma", []));

            Assert.True(sender.RecordCommand(
                new LocalPluginCommand(10u, [], "/example go", 100)));
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));

            Assert.Equal(
                100,
                Assert.Single(first.CaptureRemoteCommands(
                    0L, "Coldeve", 20u, [])).StaggerMilliseconds);
            Assert.Equal(
                200,
                Assert.Single(second.CaptureRemoteCommands(
                    0L, "Coldeve", 30u, [])).StaggerMilliseconds);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A client wearing none of the labels a line is aimed at is not a
    /// recipient, so it does not take a place in the order either: the
    /// clients that do take the line close up behind the sender.
    ///
    /// Mutation check (2026-09-22): counting every client in the order
    /// rather than only those the line is aimed at turned the wait from one
    /// delay to two.
    /// </summary>
    [Fact]
    public void AClientTheLineIsNotAimedAtTakesNoPlaceInTheOrder()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var sender = Registry(root, time, 1);
            using var bystander = Registry(root, time, 2);
            using var reader = Registry(root, time, 3);
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));
            bystander.Publish(Client(bystander.ClientId, 20u, "Beta", ["tank"]));
            reader.Publish(Client(reader.ClientId, 30u, "Gamma", ["healer"]));

            Assert.True(sender.RecordCommand(
                new LocalPluginCommand(10u, ["healer"], "/example go", 100)));
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));

            Assert.Equal(
                100,
                Assert.Single(reader.CaptureRemoteCommands(
                    0L, "Coldeve", 30u, ["healer"])).StaggerMilliseconds);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A client that crashed leaves its note in the folder for good, and the
    /// scan hands that note over like any other. It is no longer playing, so
    /// it is no longer a recipient: the clients behind it close up rather
    /// than each waiting an extra place for the rest of the session.
    ///
    /// Mutation check (2026-09-22): counting a note the folder still holds
    /// without asking whether it is recent made this client wait 300ms
    /// rather than 200ms.
    /// </summary>
    [Fact]
    public void AClientThatStoppedSayingSoTakesNoPlaceInTheOrder()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var sender = Registry(root, time, 1);
            using var live = Registry(root, time, 2);
            using var crashed = Registry(root, time, 3);
            using var reader = Registry(root, time, 4);
            // The one that stops: it says so once and never again, which is
            // what a client that crashed leaves behind.
            crashed.Publish(Client(crashed.ClientId, 30u, "Gamma", []));

            time.Advance(TimeSpan.FromSeconds(30));
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));
            live.Publish(Client(live.ClientId, 20u, "Beta", []));
            reader.Publish(Client(reader.ClientId, 40u, "Delta", []));

            Assert.True(sender.RecordCommand(
                new LocalPluginCommand(10u, [], "/example go", 100)));
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));

            // One live client is ahead of this one, so this one is second in
            // the order behind the sender and waits two delays.
            Assert.Equal(
                200,
                Assert.Single(reader.CaptureRemoteCommands(
                    0L, "Coldeve", 40u, [])).StaggerMilliseconds);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// What never enters the ring. Each row is a line that would be wrong to
    /// pass on -- and the note is written as JSON and read by another
    /// process, so a line carrying a control character or running to
    /// kilobytes is a broken or hostile client rather than something to
    /// hand a command bus.
    ///
    /// Mutation check (2026-09-22): removing the line rule from the ring
    /// turned the empty, long and control-character rows red; removing the
    /// delay rule turned the negative and minute-long rows red.
    /// </summary>
    [Theory]
    [InlineData(10u, "/example go", 0, true)]
    [InlineData(0u, "/example go", 0, false)]
    [InlineData(10u, "", 0, false)]
    [InlineData(10u, "   ", 0, false)]
    [InlineData(10u, "/example\ngo", 0, false)]
    [InlineData(10u, "/example go", -1, false)]
    [InlineData(10u, "/example go", 60_001, false)]
    [InlineData(10u, "/example go", 60_000, true)]
    public void ACommandLineThatMakesNoSenseNeverEntersTheRing(
        uint senderObjectId,
        string line,
        int delayMilliseconds,
        bool accepted)
    {
        string root = TemporaryRoot();
        try
        {
            using var sender = Registry(root, TimeProvider.System, 1);
            Assert.Equal(
                accepted,
                sender.RecordCommand(new LocalPluginCommand(
                    senderObjectId, [], line, delayMilliseconds)));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A line longer than a player could type, and one aimed at more labels
    /// than a broadcast may carry, are both refused. Separate from the rows
    /// above because the caps are what keeps thirty-two of these inside the
    /// note's size limit.
    /// </summary>
    [Fact]
    public void ALineOrALabelBeyondTheCapsIsRefused()
    {
        string root = TemporaryRoot();
        try
        {
            using var sender = Registry(root, TimeProvider.System, 1);
            Assert.False(sender.RecordCommand(new LocalPluginCommand(
                10u,
                [],
                new string('a', LocalPluginPeerRegistry.MaximumCommandLineLength + 1),
                0)));
            Assert.True(sender.RecordCommand(new LocalPluginCommand(
                10u,
                [],
                new string('a', LocalPluginPeerRegistry.MaximumCommandLineLength),
                0)));
            Assert.False(sender.RecordCommand(new LocalPluginCommand(
                10u,
                [new string('t', LocalPluginPeerRegistry.MaximumTagLength + 1)],
                "/example go",
                0)));
            Assert.False(sender.RecordCommand(new LocalPluginCommand(
                10u,
                Enumerable
                    .Range(0, LocalPluginPeerRegistry.MaximumCommandTags + 1)
                    .Select(static index => $"tag{index}")
                    .ToArray(),
                "/example go",
                0)));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The note carries its last few lines and no more, so a client that
    /// broadcasts faster than the others poll loses the oldest rather than
    /// growing the note without limit.
    /// </summary>
    [Fact]
    public void TheCommandRingKeepsOnlyItsLastLines()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var sender = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            for (int index = 0;
                index < LocalPluginPeerRegistry.CommandRingCapacity + 4;
                index++)
            {
                Assert.True(sender.RecordCommand(new LocalPluginCommand(
                    10u, [], $"/example {index}", 0)));
            }
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));

            IReadOnlyList<LocalPluginPeerCommand> read =
                reader.CaptureRemoteCommands(0L, "Coldeve", 20u, []);
            Assert.Equal(LocalPluginPeerRegistry.CommandRingCapacity, read.Count);
            Assert.Equal("/example 4", read[0].Command.Line);
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The two rings in one note are read with cursors of their own. A
    /// client that only wanted the command lines must not move the cast
    /// mark past casts it never looked at, and the sequences count from one
    /// in both rings, so a single mark would swallow one of them.
    ///
    /// Mutation check (2026-09-22): folding the two high-water marks into
    /// one turned this red -- the cast never arrived.
    /// </summary>
    [Fact]
    public void ReadingTheCommandRingLeavesTheCastRingsCursorWhereItWas()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var sender = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            Assert.True(sender.RecordCommand(
                new LocalPluginCommand(10u, [], "/example go", 0)));
            Assert.True(sender.RecordCast(Landed(10u, 0x50000012u, 42u)));
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));

            Assert.Single(reader.CaptureRemoteCommands(0L, "Coldeve", 20u, []));
            Assert.Single(reader.CaptureRemoteCasts(0L, "Coldeve", 20u));
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A client playing on another server shares a hard disk and nothing
    /// else: its object ids and its commands mean nothing here.
    /// </summary>
    [Fact]
    public void ALineFromAClientInAnotherWorldIsNotRead()
    {
        string root = TemporaryRoot();
        var time = new ManualTimeProvider(
            new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using var sender = Registry(root, time, 1);
            using var reader = Registry(root, time, 2);
            Assert.True(sender.RecordCommand(
                new LocalPluginCommand(10u, [], "/example go", 0)));
            sender.Publish(Client(sender.ClientId, 10u, "Alpha", []));

            Assert.Empty(
                reader.CaptureRemoteCommands(0L, "Darktide", 20u, []));
        }
        finally
        {
            Delete(root);
        }
    }

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
        int which) => new(root, time, Instance(which));

    /// <summary>
    /// The instance id registry number <paramref name="which"/> runs under,
    /// so a raw note can claim it or be written into its file.
    /// </summary>
    private static Guid Instance(int which) =>
        Guid.Parse($"{which:D8}-0000-0000-0000-000000000000");

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

    /// <summary>
    /// The same client as <see cref="Client"/>, standing somewhere the caller
    /// chooses -- including somewhere that is not a number.
    /// </summary>
    private static PluginNetworkClient ClientAt(
        uint clientId,
        double eastWest,
        double northSouth,
        double elevation,
        float heading)
    {
        PluginNetworkClient client = Client(clientId, 10u, "Alpha", []);
        return client with
        {
            Position = new PluginNavigationPosition(
                0x7F7F0001u, eastWest, northSouth, elevation, heading, true),
            Heading = heading,
        };
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
