using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// Telling the other clients on this computer what this one cast, and
/// reading what they cast. Two characters fighting one monster should not
/// both spend a cast landing the same debuff, and the client is what carries
/// the news between them.
///
/// Everything a plugin hands in is checked here rather than trusted: the
/// note is a file, a plugin is third-party code, and a cast nobody can make
/// sense of would either be acted on wrongly or -- in the case of a duration
/// that is not a finite number -- stop the client announcing itself at all.
/// </summary>
public sealed class RuntimeAutomationSurfaceCastSharingTests
{
    private const uint LocalPlayer = 0x5000000Au;
    private const uint Peer = 0x5000000Bu;
    private const uint Monster = 0x50000012u;
    private const uint KnownSpell = 42u;
    private const uint UnknownSpell = 4242u;

    /// <summary>
    /// The happy path on the writing side: what a plugin announces really
    /// lands in the note another client on this machine reads, with the
    /// caster, the target, the spell, the skill and the duration intact.
    ///
    /// Mutation check (2026-09-21): making <c>AnnounceCastSuccess</c> return
    /// true without recording turned this red with "no cast in the note".
    /// </summary>
    [Fact]
    public void WhatAPluginAnnouncesIsInTheNoteAnotherClientReads()
    {
        string root = TemporaryRoot();
        try
        {
            using GameRuntime runtime = Bound(root, out var surface, out var peers);
            using (surface)
            {
                Assert.True(surface.Network.AnnounceCastSuccess(
                    Monster, KnownSpell, effectiveSkill: 357, durationSeconds: 60d));
                // The client's own tick writes the note; a unit test stands in
                // for it, because nothing here has a body to publish a
                // position from.
                peers.Publish(Note(peers.ClientId, LocalPlayer, "Acdream"));

                using var onlooker = new LocalPluginPeerRegistry(root);
                PluginPeerCast only = Assert.Single(
                    onlooker.CaptureRemoteCasts(0L, string.Empty, Peer));
                Assert.Equal(LocalPlayer, only.CasterObjectId);
                Assert.Equal(Monster, only.TargetObjectId);
                Assert.Equal(KnownSpell, only.SpellId);
                Assert.Equal(357, only.EffectiveSkill);
                Assert.True(only.Landed);
                Assert.InRange(only.SecondsRemaining, 59d, 60d);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// An attempt is not a success: it carries no duration, and a reader can
    /// tell the two apart, so a plugin can hold off starting the same spell
    /// without also believing the effect is already in place.
    /// </summary>
    [Fact]
    public void AnAttemptCarriesNoDurationAndSaysSo()
    {
        string root = TemporaryRoot();
        try
        {
            using GameRuntime runtime = Bound(root, out var surface, out var peers);
            using (surface)
            {
                Assert.True(surface.Network.AnnounceCastAttempt(
                    Monster, KnownSpell, effectiveSkill: 357));
                peers.Publish(Note(peers.ClientId, LocalPlayer, "Acdream"));

                using var onlooker = new LocalPluginPeerRegistry(root);
                PluginPeerCast only = Assert.Single(
                    onlooker.CaptureRemoteCasts(0L, string.Empty, Peer));
                Assert.False(only.Landed);
                Assert.Equal(0d, only.SecondsRemaining);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// What this client refuses to say. Each row is a cast that would be
    /// wrong or dangerous to pass on, and the refusal happens before
    /// anything is written.
    ///
    /// Mutation check (2026-09-21): removing the spell-table lookup from
    /// the announce turned the unknown-spell row red, and removing the
    /// ring's own rule turned the target, skill and duration rows red.
    /// </summary>
    [Theory]
    // Nothing to attribute the effect to.
    [InlineData(0u, KnownSpell, 357, 60d, false)]
    // A spell this client's own table cannot name is never passed on: a
    // reader could not classify it, and the id could be anything.
    [InlineData(Monster, UnknownSpell, 357, 60d, false)]
    [InlineData(Monster, 0u, 357, 60d, false)]
    // A skill below zero is nonsense.
    [InlineData(Monster, KnownSpell, -1, 60d, false)]
    // Durations that are not a finite positive number of seconds. The
    // non-finite ones matter most: they cannot be written as JSON at all.
    [InlineData(Monster, KnownSpell, 357, 0d, false)]
    [InlineData(Monster, KnownSpell, 357, -5d, false)]
    [InlineData(Monster, KnownSpell, 357, double.NaN, false)]
    [InlineData(Monster, KnownSpell, 357, double.PositiveInfinity, false)]
    // A week is not a spell duration.
    [InlineData(Monster, KnownSpell, 357, 604800d, false)]
    [InlineData(Monster, KnownSpell, 0, 60d, true)]
    public void ACastThatMakesNoSenseIsNeverPassedOn(
        uint targetObjectId,
        uint spellId,
        int effectiveSkill,
        double durationSeconds,
        bool accepted)
    {
        string root = TemporaryRoot();
        try
        {
            using GameRuntime runtime = Bound(root, out var surface, out var peers);
            using (surface)
            {
                Assert.Equal(
                    accepted,
                    surface.Network.AnnounceCastSuccess(
                        targetObjectId, spellId, effectiveSkill, durationSeconds));
                peers.Publish(Note(peers.ClientId, LocalPlayer, "Acdream"));

                using var onlooker = new LocalPluginPeerRegistry(root);
                Assert.Equal(
                    accepted ? 1 : 0,
                    onlooker.CaptureRemoteCasts(0L, string.Empty, Peer).Count);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A character that is not in the world has no object id to put its name
    /// to, so it announces nothing rather than announcing a cast by nobody.
    ///
    /// Mutation check (2026-09-21): letting a cast by object id zero through
    /// both the announce and the ring turned this red.
    /// </summary>
    [Fact]
    public void AClientWithNoCharacterAnnouncesNothing()
    {
        string root = TemporaryRoot();
        try
        {
            using GameRuntime runtime = Bound(
                root, out var surface, out _, playerObjectId: 0u);
            using (surface)
            {
                Assert.False(surface.Network.AnnounceCastSuccess(
                    Monster, KnownSpell, 357, 60d));
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The reading side: a peer's cast reaches a plugin with the time LEFT
    /// rather than the total, because the note is polled and a success read
    /// five seconds late must not be applied as if it were fresh. The cursor
    /// is the plugin's own, so reading twice does not hand the same cast
    /// back and applying it twice.
    ///
    /// Mutation check (2026-09-21): handing back the published duration
    /// instead of what is left of it turned the seconds assertion red.
    /// </summary>
    [Fact]
    public void APeersCastArrivesWithTheTimeLeftAndOnlyOnce()
    {
        string root = TemporaryRoot();
        var time = new ManualTime(
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
        try
        {
            using GameRuntime runtime = Bound(
                root, out var surface, out _, time: time);
            using (surface)
            {
                using var peer = new LocalPluginPeerRegistry(root, time);
                Assert.True(peer.RecordCast(new LocalPluginCast(
                    Peer, Monster, KnownSpell, 357, 60d, Landed: true)));
                peer.Publish(Note(peer.ClientId, Peer, "Horan"));

                time.Advance(TimeSpan.FromSeconds(5));
                PluginPeerCast only = Assert.Single(
                    surface.Network.CaptureCasts(0L));
                Assert.Equal(Peer, only.CasterObjectId);
                Assert.Equal(Monster, only.TargetObjectId);
                Assert.Equal(KnownSpell, only.SpellId);
                Assert.Equal(55d, only.SecondsRemaining, 3);

                Assert.Empty(surface.Network.CaptureCasts(only.Sequence));
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A remote cast is classified in THIS client's spell table or not at
    /// all. The note carries an id and nothing else worth believing, so a
    /// spell this client cannot name never reaches a plugin.
    ///
    /// Mutation check (2026-09-21): removing the spell-table filter from the
    /// capture turned this red with one cast instead of none.
    /// </summary>
    [Fact]
    public void ASpellThisClientCannotNameNeverReachesAPlugin()
    {
        string root = TemporaryRoot();
        try
        {
            using GameRuntime runtime = Bound(root, out var surface, out _);
            using (surface)
            {
                using var peer = new LocalPluginPeerRegistry(root);
                Assert.True(peer.RecordCast(new LocalPluginCast(
                    Peer, Monster, UnknownSpell, 357, 60d, Landed: true)));
                peer.Publish(Note(peer.ClientId, Peer, "Horan"));

                Assert.Empty(surface.Network.CaptureCasts(0L));
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// Reading a peer's cast does not apply it. A plugin decides what a
    /// remote cast means; a host that quietly wrote it into the enchantment
    /// ledger would change what every plugin already installed believes
    /// about its targets without anybody asking for it.
    ///
    /// Mutation check (2026-09-21): applying a captured landed cast to the
    /// ledger inside the capture turned the "nothing tracked" assertion red.
    /// </summary>
    [Fact]
    public void ReadingAPeersCastDoesNotPutItInThisClientsEnchantmentLedger()
    {
        string root = TemporaryRoot();
        try
        {
            using GameRuntime runtime = Bound(root, out var surface, out _);
            using (surface)
            {
                using var peer = new LocalPluginPeerRegistry(root);
                Assert.True(peer.RecordCast(new LocalPluginCast(
                    Peer, Monster, KnownSpell, 357, 60d, Landed: true)));
                peer.Publish(Note(peer.ClientId, Peer, "Horan"));

                PluginPeerCast only = Assert.Single(
                    surface.Network.CaptureCasts(0L));
                Assert.Empty(surface.Enchantments.Capture(Monster));

                // The plugin's decision, not the host's: this is the call it
                // makes when it does want the remote cast counted.
                Assert.True(surface.Enchantments.ReportCast(
                    only.TargetObjectId, only.SpellId, only.SecondsRemaining));
                Assert.Single(surface.Enchantments.Capture(Monster));
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A runtime with a character, a spell table and a plugin surface whose
    /// notes go to a scratch folder rather than the player's own.
    /// </summary>
    private static GameRuntime Bound(
        string root,
        out RuntimeAutomationSurface surface,
        out LocalPluginPeerRegistry peers,
        uint playerObjectId = LocalPlayer,
        TimeProvider? time = null)
    {
        GameRuntime runtime = GameRuntimeTestFactory.Create();
        runtime.PlayerIdentity.ServerGuid = playerObjectId;
        runtime.CharacterOwner.InstallSpellMetadata(
            SpellTable.Create([DurationSpell()]));
        peers = new LocalPluginPeerRegistry(root, time);
        surface = new RuntimeAutomationSurface(events: null, peers: peers);
        surface.Bind(
            runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        return runtime;
    }

    /// <summary>
    /// A peer's note. The world name is empty because a runtime that never
    /// chose a character on a server has no world name, and the rule is that
    /// a peer naming a DIFFERENT world is skipped.
    /// </summary>
    private static PluginNetworkClient Note(
        uint clientId,
        uint playerId,
        string name) => new(
            clientId,
            playerId,
            name,
            string.Empty,
            new PluginNavigationPosition(0x7F7F0001u, 96d, 97d, 1d, 0f, true),
            [],
            100u, 100u, 100u, 100u, 100u, 100u,
            0f);

    private static SpellMetadata DurationSpell() => new(
        KnownSpell,
        "Fire Vulnerability Other VII",
        "Life Magic",
        7u,
        0u,
        string.Empty,
        60f,
        10,
        true,
        false,
        string.Empty,
        0,
        350,
        0u,
        7,
        false,
        true,
        false,
        0f,
        0u,
        0u,
        1u,
        0);

    private static string TemporaryRoot() => Path.Combine(
        Path.GetTempPath(),
        $"acdream-cast-sharing-{Guid.NewGuid():N}");

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class ManualTime(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
