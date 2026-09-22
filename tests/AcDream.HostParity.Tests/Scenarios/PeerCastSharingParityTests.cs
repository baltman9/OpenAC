using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Two characters played side by side on one machine, fighting the same
/// monster. The second one should not spend a cast landing a debuff the
/// first already landed, and the only thing that can tell it so is the
/// client: the note each client writes for the others now carries the casts
/// it made as well as where it is standing.
///
/// Both clients have to do this or a plugin's behaviour changes depending on
/// whether there is a window. The writing side rides the tick the note is
/// already written on; the reading side is a file read either client can do.
/// These scenarios drive both through <see cref="IPluginHost"/>, which is
/// the only way a plugin ever reaches it.
///
/// A value is recorded AND asserted here. Recording alone only compares the
/// two clients, and two clients agreeing on a wrong number is still wrong;
/// three of the mutations below stayed green until the assertions went in.
///
/// Mutation checks (2026-09-21):
/// * leaving the ring out of the note turned both scenarios red, with
///   "told the machine nothing about its cast" and "did not read the other
///   client's cast";
/// * handing the reader the duration that was published instead of what is
///   left of it turned
///   <see cref="EitherClientReadsWhatAnotherClientOnTheMachineCast"/> red;
/// * having the capture write a landed cast into this client's own
///   enchantment ledger turned the same scenario red;
/// * ignoring the caller's cursor turned it red again.
/// </summary>
public sealed class PeerCastSharingParityTests
{
    /// <summary>The spell both arms install so a cast has something to name.</summary>
    private const uint SharedSpell = 42u;

    /// <summary>
    /// A second client on this machine, with a fixed identity so both arms
    /// read the same client id and the two transcripts can be compared.
    /// </summary>
    private static readonly Guid OtherClient =
        Guid.Parse("7ea50000-0000-0000-0000-0000000000c7");

    /// <summary>The character that second client is playing.</summary>
    private const uint OtherCharacter = 0x50000002u;

    /// <summary>
    /// The writing side: what a plugin announces reaches the note another
    /// client on this machine reads, on either client.
    /// </summary>
    [Fact]
    public void EitherClientTellsTheMachineWhatItCast() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            InstallSpell(arm);

            INetworkAutomation network = arm.Host.Automation.Network;
            transcript.Step("announced");
            transcript.Record(
                "accepted",
                network.AnnounceCastSuccess(
                    ParityWorld.Monster,
                    SharedSpell,
                    effectiveSkill: 357,
                    durationSeconds: 60d));
            // The client writes its note on its own tick, as it does for
            // everything else it tells the machine about itself.
            arm.Advance();

            using var onlooker = new LocalPluginPeerRegistry(
                arm.PeerDirectory, timeProvider: null, OtherClient);
            PluginPeerCast[] seen = onlooker.CaptureRemoteCasts(
                0L, arm.Host.Automation.Character.WorldName, OtherCharacter)
                .ToArray();
            Assert.True(
                seen.Length == 1,
                $"{arm.Name} told the machine nothing about its cast: "
                + $"{seen.Length} casts in {arm.PeerDirectory}.");
            Record(transcript, seen[0]);
            transcript.Record(
                "isThisCharacter",
                seen[0].CasterObjectId == arm.Host.Automation.Character.ObjectId);
            Assert.Equal(
                arm.Host.Automation.Character.ObjectId, seen[0].CasterObjectId);
            Assert.Equal(ParityWorld.Monster, seen[0].TargetObjectId);
            Assert.Equal(SharedSpell, seen[0].SpellId);
            Assert.Equal(357, seen[0].EffectiveSkill);
            Assert.True(seen[0].Landed);
        });

    /// <summary>
    /// The reading side: a cast another client on this machine announced
    /// reaches a plugin, with the time LEFT rather than the duration that
    /// was published, on either client.
    /// </summary>
    [Fact]
    public void EitherClientReadsWhatAnotherClientOnTheMachineCast() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            InstallSpell(arm);

            // The other client wrote its note five seconds ago, which is the
            // whole reason a total duration is the wrong thing to hand a
            // reader: this one is polling a file, not hearing an event.
            using (var other = new LocalPluginPeerRegistry(
                arm.PeerDirectory, new FiveSecondsAgo(), OtherClient))
            {
                Assert.True(other.RecordCast(new LocalPluginCast(
                    CasterObjectId: OtherCharacter,
                    TargetObjectId: ParityWorld.Monster,
                    SpellId: SharedSpell,
                    EffectiveSkill: 357,
                    DurationSeconds: 60d,
                    Landed: true)));
                other.Publish(OtherClientNote(arm));

                transcript.Step("read");
                PluginPeerCast[] read = arm.Host.Automation.Network
                    .CaptureCasts(0L)
                    .ToArray();
                Assert.True(
                    read.Length == 1,
                    $"{arm.Name} did not read the other client's cast: "
                    + $"{read.Length} casts from {arm.PeerDirectory}.");
                Record(transcript, read[0]);
                // The reader can tell WHICH other client it came from, which
                // is how a plugin attributes a cast; the number is this
                // onlooker's own and is the same on both arms.
                Assert.Equal(other.ClientId, read[0].ClientId);
                // Sixty seconds were published five seconds ago, so a plugin
                // is handed about fifty-five. Being handed sixty would have
                // it believe a lapsed debuff was still running. Asserted
                // rather than only recorded: two clients agreeing on a wrong
                // number is still wrong.
                transcript.Record(
                    "fiveSecondsHaveComeOffTheSixtyPublished",
                    read[0].SecondsRemaining is > 54d and < 56d);
                Assert.InRange(read[0].SecondsRemaining, 54d, 56d);

                // A plugin's own cursor, not the host's: reading again from
                // where it left off hands back nothing.
                transcript.Record(
                    "afterTheCursor",
                    arm.Host.Automation.Network
                        .CaptureCasts(read[0].Sequence).Count);
                Assert.Empty(
                    arm.Host.Automation.Network.CaptureCasts(read[0].Sequence));
                // And the host did not decide for the plugin that a cast it
                // read is an effect this client believes in.
                transcript.Record(
                    "trackedWithoutBeingAskedTo",
                    arm.Host.Automation.Enchantments
                        .Capture(ParityWorld.Monster).Count);
                Assert.Empty(
                    arm.Host.Automation.Enchantments.Capture(ParityWorld.Monster));
            }
        });

    /// <summary>
    /// What the second client says about itself. Its world has to be this
    /// client's world -- a client logged in somewhere else names other
    /// creatures entirely -- and the arms have no world name, so it copies
    /// whatever this one reports.
    /// </summary>
    private static PluginNetworkClient OtherClientNote(ParityArm arm) => new(
        0u,
        OtherCharacter,
        "Onlooker",
        arm.Host.Automation.Character.WorldName,
        new PluginNavigationPosition(
            ParityPlayerBody.Cell,
            ParityWorld.PlayerX,
            ParityWorld.PlayerY,
            ParityPlayerBody.GroundHeight,
            0f,
            true),
        [],
        100u, 100u, 100u, 100u, 100u, 100u,
        0f);

    /// <summary>
    /// A spell table with the one spell these scenarios cast. A client
    /// refuses to pass on, or to read, a spell its own table cannot name, so
    /// without this both arms would agree on silence and prove nothing.
    /// </summary>
    private static void InstallSpell(ParityArm arm) =>
        arm.Runtime.CharacterOwner.InstallSpellMetadata(
            SpellTable.Create(
            [
                new SpellMetadata(
                    SharedSpell,
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
                    0),
            ]));

    private static void Record(ParityTranscript transcript, PluginPeerCast cast)
    {
        transcript.Record("sequence", cast.Sequence);
        // Which client instance announced it, not WHICH NUMBER that instance
        // drew: a client identifies itself with a fresh id every run, so the
        // number legitimately differs between two runs of the same script.
        transcript.Record("clientIdIsSet", cast.ClientId != 0u);
        transcript.Record("casterObjectId", cast.CasterObjectId);
        transcript.Record("targetObjectId", cast.TargetObjectId);
        transcript.Record("spellId", cast.SpellId);
        transcript.Record("effectiveSkill", cast.EffectiveSkill);
        transcript.Record("landed", cast.Landed);
        // The seconds themselves run on the wall clock, which is not the
        // same on two runs, so what is recorded is that they are inside the
        // duration that was published rather than the number itself.
        transcript.Record(
            "secondsRemainingWithinTheDuration",
            cast.SecondsRemaining is > 0d and <= 60d);
    }

    /// <summary>
    /// A clock five seconds behind this one, so a note written through it
    /// looks like news that has been sitting in the folder for a while.
    /// </summary>
    private sealed class FiveSecondsAgo : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(5);
    }
}
