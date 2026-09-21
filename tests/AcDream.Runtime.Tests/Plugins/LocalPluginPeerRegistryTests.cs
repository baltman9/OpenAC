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
