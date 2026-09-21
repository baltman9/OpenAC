using System.Numerics;
using AcDream.Runtime.Physics;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Another creature's body, between the server's updates, on both clients.
///
/// Why it matters: the server tells a client where a creature is a few times a
/// second and expects the client to fill the gaps itself from the cycle the
/// creature is playing. A client that does not fill them has creatures that
/// stand still and jump, a walk aimed at a creature that never re-aims, and a
/// plugin that reads a position several tenths of a second old. Until now one
/// client filled the gaps and the other did not, and the difference was
/// declared rather than closed.
///
/// Each scenario runs the same script against a client with a window and a
/// client without, and the two transcripts have to be the same text. The step
/// is the same length on both, so the agreement is exact rather than within a
/// tolerance.
///
/// Mutation checks (2026-09-20), all run:
/// * making the windowless drive carry nothing turned four of the five red,
///   and left only the beyond-the-bubble one green -- which is right, because
///   that one asserts nothing is carried;
/// * making the client with a window stop routing a creature's body through
///   the shared owner turned the same four red, so the windowed arm really is
///   running the windowed path;
/// * removing the bubble from the activity gate turned the beyond-the-bubble
///   one red;
/// * having a plugin read the server's last word about a creature instead of
///   its body turned all five red.
/// Restoring each turned them green again.
/// </summary>
public sealed class RemoteBodyParityTests
{
    /// <summary>Nine tenths of a second at the shared step length.</summary>
    private const int TicksForTheWholeWalk = 60;

    /// <summary>How often the server says where the creature is.</summary>
    private const int TicksBetweenServerUpdates = 15;

    [Fact]
    public void ACreatureWalkingBetweenSparseUpdatesLandsInTheSamePlaceOnBoth() =>
        RemoteBodyScenario.Run(static (arm, transcript) =>
        {
            Stage(arm, transcript);

            for (int tick = 1; tick <= TicksForTheWholeWalk; tick++)
            {
                arm.Tick(RemoteBodyArm.TickSeconds);
                if (tick % TicksBetweenServerUpdates != 0)
                    continue;

                // The server's word, arriving where a creature walking east
                // at three metres a second would be by now.
                float seconds = tick * RemoteBodyArm.TickSeconds;
                transcript.Step($"after {tick} steps");
                RuntimeRemoteContactArm routed =
                    arm.ServerSaysTheCreatureIsAt(
                        RemoteBodyArm.Start
                            + new Vector3(3f * seconds, 0f, 0f));
                transcript.Record("routed", routed.ToString());
                Record(arm, transcript);
            }

            // Standing still would agree line for line and prove nothing, so
            // insist the creature really walked.
            Assert.True(
                arm.Body.Body.Position.X > RemoteBodyArm.Start.X + 1f,
                $"The creature never walked: it is at "
                + $"{arm.Body.Body.Position.X:0.00} against a start of "
                + $"{RemoteBodyArm.Start.X:0.00}.");
        });

    /// <summary>
    /// The server putting the creature somewhere far from where this client
    /// has it: the body is re-placed rather than asked to catch up, and both
    /// clients re-place it in the same spot.
    /// </summary>
    [Fact]
    public void AFarSnapPutsTheBodyInTheSamePlaceOnBoth() =>
        RemoteBodyScenario.Run(static (arm, transcript) =>
        {
            Stage(arm, transcript);
            for (int tick = 0; tick < 10; tick++)
                arm.Tick(RemoteBodyArm.TickSeconds);

            transcript.Step("the server says it is thirty metres away");
            RuntimeRemoteContactArm routed = arm.ServerSaysTheCreatureIsAt(
                RemoteBodyArm.Start + new Vector3(30f, 0f, 0f));
            transcript.Record("routed", routed.ToString());
            Record(arm, transcript);

            transcript.Step("and a few steps later");
            for (int tick = 0; tick < 10; tick++)
                arm.Tick(RemoteBodyArm.TickSeconds);
            Record(arm, transcript);

            Assert.True(
                arm.Body.Body.Position.X > RemoteBodyArm.Start.X + 20f,
                "The body was never re-placed, so this proves nothing: it is "
                + $"at {arm.Body.Body.Position.X:0.00}.");
        });

    /// <summary>
    /// The server moving the creature outright. Both clients move the body
    /// there, and both then carry it on from the same place.
    /// </summary>
    [Fact]
    public void ATeleportPutsTheBodyInTheSamePlaceOnBoth() =>
        RemoteBodyScenario.Run(static (arm, transcript) =>
        {
            Stage(arm, transcript);
            for (int tick = 0; tick < 10; tick++)
                arm.Tick(RemoteBodyArm.TickSeconds);

            transcript.Step("the server moves it outright");
            Vector3 movedTo = RemoteBodyArm.Start + new Vector3(0f, 12f, 0f);
            RuntimeRemoteContactArm routed =
                arm.ServerSaysTheCreatureIsAt(movedTo, teleported: true);
            transcript.Record("routed", routed.ToString());
            Record(arm, transcript);
            // The body is PUT where the server said rather than asked to
            // walk there: a client that interpolated instead would be twelve
            // metres behind for the next second, and two clients that both
            // left the body where it was would agree line for line.
            Assert.Equal(RuntimeRemoteContactArm.TeleportPlacement, routed);
            Assert.Equal(movedTo.X, arm.Body.Body.Position.X, 3);
            Assert.Equal(movedTo.Y, arm.Body.Body.Position.Y, 3);

            transcript.Step("and a few steps later");
            for (int tick = 0; tick < 10; tick++)
                arm.Tick(RemoteBodyArm.TickSeconds);
            Record(arm, transcript);
            // And it carries on from there rather than sliding back to where
            // it was: a tenth of a second of walking, not twelve metres of it.
            Assert.True(
                Vector3.Distance(arm.Body.Body.Position, movedTo) < 1f,
                $"The body left the place it was put: it is at "
                + $"{arm.Body.Body.Position} against {movedTo}.");
        });

    /// <summary>
    /// A creature that arrives after this client is already in the world: it
    /// still gets a body off the first word the server says about where it is,
    /// and it still walks. This is the one that used to be a difference rather
    /// than a tolerance -- a client with no window gave such a creature no
    /// body at all, so it stood at its spawn for the rest of the session.
    /// </summary>
    [Fact]
    public void ACreatureArrivingLaterStillGetsABodyAndWalksOnBoth() =>
        RemoteBodyScenario.Run(static (arm, transcript) =>
        {
            // Nine tenths of a second of world with nothing in it.
            for (int tick = 0; tick < TicksForTheWholeWalk; tick++)
                arm.Tick(RemoteBodyArm.TickSeconds);

            transcript.Step("the creature arrives");
            arm.CreatureArrives();
            transcript.Record("hasBody", arm.HasBody);
            arm.GiveTheCreatureABody();
            transcript.Record("hasBodyNow", arm.HasBody);
            Record(arm, transcript);

            transcript.Step("and it walks");
            for (int tick = 0; tick < 30; tick++)
                arm.Tick(RemoteBodyArm.TickSeconds);
            Record(arm, transcript);

            Assert.True(
                arm.Body.Body.Position.X > RemoteBodyArm.Start.X + 1f,
                "A creature that arrived late never walked: it is at "
                + $"{arm.Body.Body.Position.X:0.00}.");
        });

    /// <summary>
    /// A creature further off than the bubble a client keeps alive is not
    /// carried on either client, which is what keeps a crowded landblock
    /// affordable.
    /// </summary>
    [Fact]
    public void ACreatureBeyondTheBubbleIsNotCarriedOnEither() =>
        RemoteBodyScenario.Run(static (arm, transcript) =>
        {
            Stage(arm, transcript);
            arm.PlayerPosition =
                RemoteBodyArm.Start + new Vector3(500f, 0f, 0f);

            transcript.Step("nine tenths of a second later");
            for (int tick = 0; tick < TicksForTheWholeWalk; tick++)
                arm.Tick(RemoteBodyArm.TickSeconds);
            Record(arm, transcript);

            Assert.Equal(
                RemoteBodyArm.Start.X,
                arm.Body.Body.Position.X,
                4);
        });

    /// <summary>A creature in front of the character, with a body.</summary>
    private static void Stage(RemoteBodyArm arm, ParityTranscript transcript)
    {
        arm.CreatureArrives();
        arm.GiveTheCreatureABody();
        transcript.Step("staged");
        Record(arm, transcript);
    }

    /// <summary>
    /// Where the body is, what it is standing on, which cell it is in, and
    /// what a plugin is told -- which has to be the body and not the server's
    /// last word about it.
    /// </summary>
    private static void Record(RemoteBodyArm arm, ParityTranscript transcript)
    {
        transcript.Record("body.x", arm.Body.Body.Position.X);
        transcript.Record("body.y", arm.Body.Body.Position.Y);
        transcript.Record("body.z", arm.Body.Body.Position.Z);
        transcript.Record("body.walkable", arm.Body.Body.OnWalkable);
        transcript.Record("body.contact", arm.Body.Body.InContact);
        transcript.Record("body.cell", arm.Body.CellId);

        AcDream.Plugin.Abstractions.PluginNavigationPosition plugin =
            arm.PluginPosition();
        transcript.Record("plugin.cell", plugin.CellId);
        transcript.Record("plugin.eastWest", plugin.EastWest);
        transcript.Record("plugin.northSouth", plugin.NorthSouth);

        // Said outright rather than left to the transcript: a plugin is told
        // where the BODY is. Reading the server's last word about the
        // creature instead would agree on both clients and be several tenths
        // of a second stale on both.
        Assert.Equal(
            AcDream.Runtime.Gameplay.RuntimeWorldObjectProjection
                .ProjectNavigationPosition(arm.Body.Body.CellPosition),
            plugin);
    }
}
