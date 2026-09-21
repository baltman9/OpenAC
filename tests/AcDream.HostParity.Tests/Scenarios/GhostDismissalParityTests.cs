using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Letting go of an object the client still believes in. A bot needs this
/// when the server has stopped talking about something the client is still
/// holding: until the object is dropped, every "what is near me" answer the
/// plugin gets carries a creature that is not there.
///
/// It used to be done by windowed-host code, so a client without a window
/// answered that the operation did not exist at all. It is now one runtime
/// owner over the entity directory, and what a plugin is told -- the status,
/// and whether the object is still known afterwards -- is the same on both.
/// </summary>
public sealed class GhostDismissalParityTests
{
    /// <summary>
    /// Mutation check: give the runtime owner back the windowed host's
    /// capability (or make <c>Dismiss</c> answer before the object is really
    /// gone) and the windowless arm records <c>Unavailable</c> where the
    /// windowed one records <c>Stopped</c>.
    /// </summary>
    [Fact]
    public void DismissingAGhostAnswersTheSameAndDropsTheSameObject() =>
        ParityScenario.Run((arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            ICombatAutomation combat = arm.Host.Automation.Combat;
            INavigationAutomation navigation = arm.Host.Automation.Navigation;

            transcript.Step("before");
            Record(transcript, navigation, ParityWorld.Monster);
            Record(transcript, navigation, ParityWorld.SecondMonster);

            transcript.Step("dismiss the monster");
            PluginCombatCommandResult dropped =
                combat.DismissGhostTarget(ParityWorld.Monster);
            transcript.Record("status", dropped.Status);
            // The two clients agreeing on "it did not work" would be parity
            // and no use to anybody, so the scenario also says what the
            // right answer is: the object is dropped, and only that object.
            Assert.Equal(PluginCombatCommandStatus.Stopped, dropped.Status);
            Assert.False(
                navigation.TryGetObject(ParityWorld.Monster, out _),
                $"{arm.Name}: the dismissed object is still known.");
            Assert.True(
                navigation.TryGetObject(ParityWorld.SecondMonster, out _),
                $"{arm.Name}: dismissing one object took another with it.");
            Record(transcript, navigation, ParityWorld.Monster);
            Record(transcript, navigation, ParityWorld.SecondMonster);
            arm.Advance();

            transcript.Step("dismiss it a second time");
            PluginCombatCommandResult again =
                combat.DismissGhostTarget(ParityWorld.Monster);
            transcript.Record("status", again.Status);
            Assert.Equal(PluginCombatCommandStatus.InvalidTarget, again.Status);
            Record(transcript, navigation, ParityWorld.Monster);
            arm.Advance();

            transcript.Step("dismiss the character itself");
            PluginCombatCommandResult self =
                combat.DismissGhostTarget(ParityWorld.Player);
            transcript.Record("status", self.Status);
            Assert.Equal(PluginCombatCommandStatus.InvalidTarget, self.Status);
            Assert.True(
                navigation.TryGetObject(ParityWorld.Player, out _),
                $"{arm.Name}: the character dismissed itself.");
            Record(transcript, navigation, ParityWorld.Player);
            arm.Advance();

            transcript.Step("dismiss nothing at all");
            transcript.Record("status", combat.DismissGhostTarget(0u).Status);
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Whether the client still believes in the object, asked the way a
    /// plugin asks.
    /// </summary>
    private static void Record(
        ParityTranscript transcript,
        INavigationAutomation navigation,
        uint objectId) =>
        transcript.Record(
            $"known[0x{objectId:X8}]",
            navigation.TryGetObject(objectId, out _));
}
