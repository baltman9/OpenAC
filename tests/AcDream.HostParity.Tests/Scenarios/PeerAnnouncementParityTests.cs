using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

/// <summary>
/// How this client tells the other clients on the same machine that it is
/// here: a small note it rewrites on a timer with who it is, where it stands
/// and which words it answers to. A plugin running on one client reads the
/// other clients' notes to decide who is in its group, so a client that never
/// writes one is invisible to every plugin on the machine -- which is what the
/// windowless client was, because it built its plugin surface without the tick
/// the note is written on and without the configured words.
///
/// These scenarios assert the effect outright rather than comparing two
/// silences: the note has to exist, and it has to carry this character and
/// this client's configured words.
///
/// Mutation checks (2026-09-20):
/// * building the windowless host's surface with no tick (its state before
///   this work) turned <see cref="EitherClientAnnouncesItselfToTheMachine"/>
///   red with "windowless announced nothing";
/// * dropping the configured words from the windowless host's surface inputs
///   turned <see cref="EitherClientAnnouncesItsConfiguredWords"/> red.
/// </summary>
public sealed class PeerAnnouncementParityTests
{
    /// <summary>
    /// The note itself: after one step past the heartbeat, a second client
    /// looking in the same folder finds this one, with this character's id and
    /// name.
    /// </summary>
    [Fact]
    public void EitherClientAnnouncesItselfToTheMachine() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            arm.Advance();

            PluginNetworkClient announced = TheOneAnnouncement(arm);
            ICharacterInfo character = arm.Host.Automation.Character;

            transcript.Step("announced");
            transcript.Record("playerId", announced.PlayerId);
            transcript.Record("isTheCharacter", announced.PlayerId == character.ObjectId);
            transcript.Record("name", announced.Name);
            transcript.Record("worldName", announced.WorldName);
            transcript.Record("health", announced.CurrentHealth);
            transcript.Record("maxHealth", announced.MaxHealth);
            transcript.Record("stamina", announced.CurrentStamina);
            transcript.Record("mana", announced.CurrentMana);
        });

    /// <summary>
    /// The configured words, which are how a plugin tells one bot apart from
    /// another. Both clients read them from their own configuration, so both
    /// have to carry them into the note.
    /// </summary>
    [Fact]
    public void EitherClientAnnouncesItsConfiguredWords() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            arm.Advance();

            PluginNetworkClient announced = TheOneAnnouncement(arm);

            transcript.Step("words");
            transcript.Record("count", announced.Tags.Count);
            transcript.Record("words", string.Join(",", announced.Tags));
            Assert.Equal(
                ParityArm.ConfiguredPluginTags,
                announced.Tags.ToArray());
        });

    /// <summary>
    /// What a second client on this machine sees in this arm's folder. A
    /// registry skips its own note, so a fresh one reads this client's note
    /// exactly as another client would.
    /// </summary>
    private static PluginNetworkClient TheOneAnnouncement(ParityArm arm)
    {
        using var onlooker = new LocalPluginPeerRegistry(arm.PeerDirectory);
        IReadOnlyList<PluginNetworkClient> seen =
            onlooker.CaptureRemoteClients();
        Assert.True(
            seen.Count == 1,
            $"{arm.Name} announced nothing another client on this machine "
            + $"could see: {seen.Count} notes in {arm.PeerDirectory}.");
        return seen[0];
    }
}
