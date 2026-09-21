using System.Numerics;
using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The character running and turning under its own animation cycles, on both
/// clients.
///
/// This is the difference the whole of this file is about. A character carries
/// itself forward by the cycles it is playing: how far one step of a run cycle
/// takes it, and how far round one step of a turn cycle turns it, are the
/// character's own numbers and not the client's. Only the client with a window
/// used to set that up; the other one shoved the body along by its declared
/// speed and turned at a fixed rate, which covered ground about a tenth slower
/// and left the character off its bearing coming out of a corner.
///
/// Both arms are given the same hand-built cycles and the same input script
/// for the same simulated time, and must end in the same place facing the same
/// way. The client with a window builds the poses of the character's parts
/// along the way and the other does not, which is exactly the split that has
/// to make no difference to where the character ends up.
///
/// Mutation check (2026-09-20), run: making the windowless advance carry
/// nothing -- the one line that asks the cycle how far it means to go --
/// turned this file red on position and heading in every phase, and green
/// again when it was put back.
/// </summary>
public sealed class LocalMotionParityTests
{
    /// <summary>Roughly two thirds of a second at the shared step length.</summary>
    private const int TicksForTwoThirdsOfASecond = 45;

    [Fact]
    public void RunningAndTurningEndsInTheSamePlaceOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            // The character's own cycles, from one hand-built source both
            // clients read. Bound before the body is made, because the body
            // asks for its cycles as soon as it has one.
            arm.Runtime.EntityObjects.Physics.BindMotionContentSource(
                new ParityMotionContent());
            _ = ParityWorld.Stage(arm);
            INavigationAutomation navigation = arm.Host.Automation.Navigation;
            _ = arm.Operations.TakeOutbound();

            transcript.Step("standing");
            transcript.Record("body", arm.HasLiveBody);
            RecordWhere(transcript, navigation);
            double startedNorth = navigation.Snapshot.Position.NorthSouth;
            double startedHeading = navigation.Snapshot.Position.HeadingDegrees;

            transcript.Step("run straight");
            transcript.Record(
                "plugin.status",
                navigation.SetMovementIntent(
                    new PluginMovementIntent(Forward: true, Run: true)));
            Advance(arm, TicksForTwoThirdsOfASecond);
            RecordWhere(transcript, navigation);
            double ranTo = navigation.Snapshot.Position.NorthSouth;

            transcript.Step("run round a corner");
            transcript.Record(
                "plugin.status",
                navigation.SetMovementIntent(new PluginMovementIntent(
                    Forward: true,
                    TurnRight: true,
                    Run: true)));
            Advance(arm, TicksForTwoThirdsOfASecond);
            RecordWhere(transcript, navigation);
            double turnedHeading = navigation.Snapshot.Position.HeadingDegrees;

            transcript.Step("run on");
            transcript.Record(
                "plugin.status",
                navigation.SetMovementIntent(
                    new PluginMovementIntent(Forward: true, Run: true)));
            Advance(arm, TicksForTwoThirdsOfASecond);
            RecordWhere(transcript, navigation);

            transcript.Step("let go");
            transcript.Record("plugin.status", navigation.ClearMovementIntent());
            transcript.Record("plugin.stop", navigation.StopMoving());
            Advance(arm, TicksForTwoThirdsOfASecond);
            RecordWhere(transcript, navigation);

            // Both clients standing still, or both refusing to turn, would
            // agree line for line and prove nothing. So insist that this
            // client really ran and really came round.
            Assert.NotEqual(startedNorth, ranTo, precision: 4);
            Assert.NotEqual(startedHeading, turnedHeading, precision: 2);
        });

    [Fact]
    public void TurningOnTheSpotTurnsTheSameAmountOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            arm.Runtime.EntityObjects.Physics.BindMotionContentSource(
                new ParityMotionContent());
            _ = ParityWorld.Stage(arm);
            INavigationAutomation navigation = arm.Host.Automation.Navigation;
            _ = arm.Operations.TakeOutbound();

            transcript.Step("standing");
            RecordWhere(transcript, navigation);
            double startedHeading = navigation.Snapshot.Position.HeadingDegrees;
            double startedNorth = navigation.Snapshot.Position.NorthSouth;
            double startedEast = navigation.Snapshot.Position.EastWest;

            transcript.Step("turn on the spot");
            transcript.Record(
                "plugin.status",
                navigation.SetMovementIntent(
                    new PluginMovementIntent(TurnRight: true, Run: true)));
            Advance(arm, TicksForTwoThirdsOfASecond);
            RecordWhere(transcript, navigation);

            transcript.Step("stop turning");
            transcript.Record("plugin.status", navigation.ClearMovementIntent());
            transcript.Record("plugin.stop", navigation.StopMoving());
            Advance(arm, TicksForTwoThirdsOfASecond);
            RecordWhere(transcript, navigation);

            // Turning on the spot is a turn and nothing else: the character
            // comes round without leaving the spot it stood on.
            Assert.NotEqual(
                startedHeading,
                navigation.Snapshot.Position.HeadingDegrees,
                precision: 2);
            Assert.Equal(
                startedNorth,
                navigation.Snapshot.Position.NorthSouth,
                precision: 5);
            Assert.Equal(
                startedEast,
                navigation.Snapshot.Position.EastWest,
                precision: 5);
        });

    /// <summary>
    /// A movement the character is sent on rather than steered through: face
    /// this way, then walk to that spot, and tell me when you are there.
    ///
    /// This is the other half of a character having cycles of its own. A
    /// cycle the character is told to play stays outstanding until something
    /// reports that it finished, and a sent movement refuses to take its next
    /// step while one is. A client that reported nothing left the character
    /// standing on the spot for the rest of the session: every walk to a
    /// thing, every walk to a place, and every turn to a heading, on a client
    /// that has the content those cycles are built from.
    ///
    /// Both arms are given the same movements at the same simulated time and
    /// must finish them the same way and in the same place.
    ///
    /// Mutation check (2026-09-20), run: taking the report of a finished
    /// cycle back out of either client turned this red on that client, with
    /// the character standing where it started and the movement still
    /// running; putting it back turned it green.
    /// </summary>
    [Fact]
    public void ASentMovementFinishesOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            arm.Runtime.EntityObjects.Physics.BindMotionContentSource(
                new ParityMotionContent());
            _ = ParityWorld.Stage(arm);
            INavigationAutomation navigation = arm.Host.Automation.Navigation;
            _ = arm.Operations.TakeOutbound();

            // A few steps of ordinary running first: the character's cycles
            // are built from content and it is given them on the first step
            // it takes, and taking hold of them clears whatever it was told
            // to do beforehand.
            transcript.Step("the session is running");
            Advance(arm, ticks: 3);
            transcript.Record("body", arm.HasLiveBody);
            RecordWhere(transcript, navigation);
            PlayerMovementController controller = Controller(arm);

            transcript.Step("face east");
            transcript.Record("accepted", controller.RequestTurnToHeading(90f));
            Advance(arm, TicksForTwoThirdsOfASecond * 3);
            RecordWhere(transcript, navigation);
            transcript.Record("still turning", IsMoving(controller));
            Assert.False(
                IsMoving(controller),
                "The character never finished coming round.");
            Assert.Equal(90d, Heading(controller), precision: 0);

            transcript.Step("walk four metres north");
            Vector3 fourMetresNorth = controller.Position + new Vector3(0f, 4f, 0f);
            transcript.Record("accepted", WalkTo(controller, fourMetresNorth));
            Advance(arm, TicksForTwoThirdsOfASecond * 6);
            RecordWhere(transcript, navigation);
            transcript.Record("still walking", IsMoving(controller));
            Assert.False(
                IsMoving(controller),
                "The character never finished the walk.");
            Assert.True(
                MathF.Abs(controller.Position.Y - fourMetresNorth.Y) < 1f,
                $"The walk ended {controller.Position.Y:0.00} rather than "
                + $"{fourMetresNorth.Y:0.00} along.");
        });

    private static PlayerMovementController Controller(ParityArm arm) =>
        Assert.IsType<PlayerMovementController>(
            arm.Runtime.MovementOwner.Controller);

    private static bool IsMoving(PlayerMovementController controller) =>
        controller.Movement.MoveTo?.IsMovingTo() == true;

    private static double Heading(PlayerMovementController controller)
    {
        double heading = MoveToMath.HeadingFromYaw(controller.Yaw);
        return ((heading % 360d) + 360d) % 360d;
    }

    private static bool WalkTo(
        PlayerMovementController controller,
        Vector3 position)
    {
        controller.Movement.MakeMoveToManager();
        return controller.Movement.PerformMovement(new MovementStruct
        {
            Type = MovementType.MoveToPosition,
            Pos = new Position(controller.CellId, position, Quaternion.Identity),
            // Half a metre of it is close enough, as an arrival always
            // has some width: a walk asked to land on a point exactly walks
            // back and forth across it forever.
            Params = new MovementParameters { DistanceToObject = 0.5f },
        }) == WeenieError.None;
    }

    private static void Advance(ParityArm arm, int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
            arm.Advance();
    }

    private static void RecordWhere(
        ParityTranscript transcript,
        INavigationAutomation navigation)
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
    }
}
