using AcDream.Runtime.Gameplay;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A character actually walking, and what each client then believes about
/// where it is, how far away a creature is, and what it told the server.
/// With no body neither client moved at all, so this is the first time the
/// two have been compared while the character is under way.
///
/// The plugin's own movement intent is set through the runtime's movement
/// owner rather than through <see cref="INavigationAutomation.SetMovementIntent"/>:
/// that seam needs each host's session commands bound, which the harness does
/// not build yet. What the plugin call answers is recorded anyway, so the day
/// it starts working the two clients have to agree about it. Below that seam
/// this is the real thing -- each host's own movement input source reads the
/// held intent and each host's own frame driver walks the body on it.
///
/// Mutation check (2026-09-20), run: dropping the post-network half of the
/// frame -- the half that sends the character's position -- from the windowed
/// arm alone turned both scenarios in this file red, along with four combat
/// ones, on outbound counts and on where each client thought the character
/// was. Restoring it turned them green.
/// </summary>
public sealed class MovementParityTests
{
    /// <summary>Roughly a fifth of a second at the shared step length.</summary>
    private const int TicksForTwoHundredMilliseconds = 13;

    [Fact]
    public void HoldingForwardWalksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            INavigationAutomation navigation = arm.Host.Automation.Navigation;

            transcript.Step("standing");
            transcript.Record("body", arm.HasLiveBody);
            RecordWhere(transcript, navigation);
            _ = arm.Operations.TakeOutbound();

            transcript.Step("hold forward");
            transcript.Record(
                "plugin.status",
                navigation.SetMovementIntent(
                    new PluginMovementIntent(Forward: true)));
            arm.Deliver(static runtime => runtime.MovementOwner.SetCommandInput(
                new MovementInput(Forward: true, Run: true)));
            for (int step = 0; step < TicksForTwoHundredMilliseconds; step++)
                arm.Advance();
            RecordWhere(transcript, navigation);
            transcript.RecordOutbound(arm);

            transcript.Step("let go");
            transcript.Record("plugin.status", navigation.ClearMovementIntent());
            arm.Deliver(static runtime =>
                runtime.MovementOwner.ClearCommandInput());
            for (int step = 0; step < TicksForTwoHundredMilliseconds; step++)
                arm.Advance();
            RecordWhere(transcript, navigation);
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void TurningOnTheSpotRunsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            INavigationAutomation navigation = arm.Host.Automation.Navigation;
            _ = arm.Operations.TakeOutbound();

            transcript.Step("turn to face east");
            transcript.Record("plugin.status", navigation.FaceHeading(90f));
            transcript.Record(
                "turned",
                arm.Runtime.MovementOwner.TurnToHeading(90f));
            for (int step = 0; step < TicksForTwoHundredMilliseconds * 4; step++)
                arm.Advance();
            RecordWhere(transcript, navigation);
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Where the character thinks it is and how far off the creatures are.
    /// The distance to a creature is what a bot decides to swing on, so the
    /// two clients disagreeing about it is the whole difference in a
    /// sentence.
    /// </summary>
    private static void RecordWhere(
        ParityTranscript transcript, INavigationAutomation navigation)
    {
        PluginNavigationSnapshot snapshot = navigation.Snapshot;
        transcript.Record("available", snapshot.IsAvailable);
        transcript.Record("moving", snapshot.IsMoving);
        transcript.Record("airborne", snapshot.IsAirborne);
        transcript.Record("cell", snapshot.Position.CellId);
        transcript.Record("eastWest", snapshot.Position.EastWest);
        transcript.Record("northSouth", snapshot.Position.NorthSouth);
        transcript.Record("elevation", snapshot.Position.Elevation);
        transcript.Record("heading", snapshot.Position.HeadingDegrees);
        RecordDistance(transcript, navigation, "monster", ParityWorld.Monster);
        RecordDistance(
            transcript, navigation, "second", ParityWorld.SecondMonster);
    }

    private static void RecordDistance(
        ParityTranscript transcript,
        INavigationAutomation navigation,
        string name,
        uint objectId)
    {
        if (!navigation.TryGetObject(objectId, out PluginNavigationObject value))
        {
            transcript.Record($"{name}.known", false);
            return;
        }
        transcript.Record($"{name}.known", true);
        transcript.Record(
            $"{name}.distance",
            navigation.Snapshot.Position.HorizontalDistanceMeters(
                value.Position));
    }
}
