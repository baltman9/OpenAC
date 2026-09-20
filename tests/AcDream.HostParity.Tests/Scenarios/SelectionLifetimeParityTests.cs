using AcDream.Core.Physics;

namespace AcDream.HostParity.Tests;

/// <summary>
/// What the player has selected, when the thing selected stops being there.
/// The server takes the object out of the world, or stops showing it, and both
/// clients have to let go of it -- otherwise a plugin reading the selection is
/// handed a guid nothing will answer to, and every command it builds on that
/// guid is refused.
///
/// Both of these used to be answered only by the client with a window: one
/// lambda on the drawing side for removal and one for hiding. The client
/// without a window kept its selection pointing at the gone object for the
/// rest of the session.
///
/// Everything here arrives the way the server sends it: the scripted message
/// goes into the arm's own world connection and travels that client's real
/// inbound route.
///
/// Both were red before the move: with the clears living on the drawing
/// side, neither arm cleared, the two transcripts agreed, and the outright
/// assertion below is what caught it.
///
/// Mutation check (2026-09-20), both run: taking
/// <c>RuntimeEntityChange.Deleted</c> out of the runtime's selection follower
/// turned <see cref="RemovingTheSelectedObjectClearsTheSelectionOnBothClients"/>
/// red; taking <c>Hidden</c> out turned
/// <see cref="HidingTheSelectedObjectClearsTheSelectionOnBothClients"/> red.
/// Restoring each turned them green.
/// </summary>
public sealed class SelectionLifetimeParityTests
{
    [Fact]
    public void RemovingTheSelectedObjectClearsTheSelectionOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Stage(arm, transcript);

            transcript.Step("the server takes it out of the world");
            arm.Server.DeleteObject(ParityWorld.Monster, instanceSequence: 1);
            arm.Advance();
            RecordSelection(transcript, arm);
            // Said outright as well as compared: two clients that BOTH kept
            // the gone object would agree, and agreeing is not the point.
            Assert.Null(arm.Host.Selection.SelectedObjectId);
            transcript.Record(
                "known",
                arm.Runtime.EntityObjects.Entities.TryGetActive(
                    ParityWorld.Monster, out _));
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void HidingTheSelectedObjectClearsTheSelectionOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Stage(arm, transcript);

            transcript.Step("the server stops showing it");
            arm.Server.SetState(
                ParityWorld.Monster,
                PhysicsStateFlags.Hidden,
                instanceSequence: 1,
                stateSequence: 2);
            arm.Advance();
            RecordSelection(transcript, arm);
            Assert.Null(arm.Host.Selection.SelectedObjectId);
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Something else going away leaves the selection alone: a scenario that
    /// only ever clears would pass just as well if the clients cleared on
    /// every message.
    /// </summary>
    [Fact]
    public void RemovingSomethingElseLeavesTheSelectionAloneOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            Stage(arm, transcript);

            transcript.Step("the server takes a different creature out");
            arm.Server.DeleteObject(
                ParityWorld.SecondMonster, instanceSequence: 1);
            arm.Advance();
            RecordSelection(transcript, arm);
            Assert.Equal(ParityWorld.Monster, arm.Host.Selection.SelectedObjectId);
            transcript.RecordOutbound(arm);
        });

    private static void Stage(ParityArm arm, ParityTranscript transcript)
    {
        _ = ParityWorld.Stage(arm);
        _ = arm.Operations.TakeOutbound();

        transcript.Step("select a creature");
        transcript.Record(
            "selected", arm.Host.Selection.Select(ParityWorld.Monster));
        RecordSelection(transcript, arm);
    }

    private static void RecordSelection(ParityTranscript transcript, ParityArm arm)
    {
        transcript.Record("selection", arm.Host.Selection.SelectedObjectId);
        transcript.Record("previous", arm.Host.Selection.PreviousObjectId);
    }
}
