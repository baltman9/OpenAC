using AcDream.Runtime.Gameplay;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A character actually walking, and what each client then believes about
/// where it is, how far away a creature is, and what it told the server.
/// With no body neither client moved at all, so this is the first time the
/// two have been compared while the character is under way.
///
/// The character is moved the way a plugin moves it and no other way: each
/// arm binds its own client's real command adapter, so a held intent, a turn
/// and letting go all go in through the plugin seam, down through that
/// client's own movement commands, and out through its own frame driver.
/// Until now the windowed client's adapter could not be built at all without
/// a presentation tree, so these calls were recorded as refused and the two
/// clients were only compared below the seam.
///
/// Mutation check (2026-09-21), run: overshooting a turn by five degrees
/// turned <see cref="TurningOnTheSpotRunsTheSameOnBothClients"/> red on both
/// arms at once, 95 against 90.
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
            double startedAt = navigation.Snapshot.Position.NorthSouth;
            _ = arm.Operations.TakeOutbound();

            transcript.Step("hold forward");
            transcript.Record(
                "plugin.status",
                navigation.SetMovementIntent(
                    new PluginMovementIntent(Forward: true, Run: true)));
            for (int step = 0; step < TicksForTwoHundredMilliseconds; step++)
                arm.Advance();
            RecordWhere(transcript, navigation);
            transcript.RecordOutbound(arm);
            // Both clients agreeing that the character never moved would
            // prove nothing, so insist that the plugin's own call moved it.
            Assert.NotEqual(
                startedAt,
                navigation.Snapshot.Position.NorthSouth,
                precision: 3);

            transcript.Step("let go");
            transcript.Record("plugin.status", navigation.ClearMovementIntent());
            transcript.Record("plugin.stop", navigation.StopMoving());
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
            double startedAtEast = navigation.Snapshot.Position.EastWest;
            double startedAtNorth = navigation.Snapshot.Position.NorthSouth;

            transcript.Step("turn to face east");
            PluginNavigationCommandStatus status = navigation.FaceHeading(East);
            transcript.Record("plugin.status", status);
            for (int step = 0; step < TicksForTheWholeTurn; step++)
                arm.Advance();
            RecordWhere(transcript, navigation);
            transcript.RecordOutbound(arm);

            // The turn was taken, and it really got there: a client that
            // turned at a different rate, or not at all, ends up facing
            // somewhere else. Two clients both facing north would agree
            // line for line and prove nothing.
            Assert.Equal(PluginNavigationCommandStatus.Accepted, status);
            Assert.Equal(
                East, navigation.Snapshot.Position.HeadingDegrees, 0);
            // And on the spot: turning is not walking, so the character is
            // where it started and the creatures are as far off as they
            // were -- three metres and seven.
            Assert.Equal(
                startedAtEast, navigation.Snapshot.Position.EastWest, 3);
            Assert.Equal(
                startedAtNorth, navigation.Snapshot.Position.NorthSouth, 3);
            Assert.Equal(3d, Distance(navigation, ParityWorld.Monster), 3);
            Assert.Equal(
                7d, Distance(navigation, ParityWorld.SecondMonster), 3);
        });

    /// <summary>A quarter turn to the right, in degrees.</summary>
    private const float East = 90f;

    /// <summary>
    /// Long enough for a quarter turn to finish at the shared step length:
    /// the character turns at something under a hundred degrees a second.
    /// </summary>
    private const int TicksForTheWholeTurn = 150;

    /// <summary>How far off something is, across the ground, in metres.</summary>
    private static double Distance(
        INavigationAutomation navigation, uint objectId) =>
        navigation.TryGetObject(objectId, out PluginNavigationObject value)
            ? navigation.Snapshot.Position.HorizontalDistanceMeters(
                value.Position)
            : double.NaN;

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
