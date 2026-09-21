using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Ui;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Stepping the selection from one nearby person to the next. Cycling used to
/// be an input action of the windowed client, so a plugin without a window
/// was told the three cycling commands did nothing; a plugin that picks its
/// target this way had no target at all in a bot.
///
/// The cycle is now one ordering over the entity directory, so both clients
/// step through the same people in the same order. The scenario stages three
/// characters at known distances and walks out and back in, which is the only
/// way to tell a real cycle from a lucky first answer.
/// </summary>
public sealed class SelectionCycleParityTests
{
    /// <summary>The three people standing about, nearest first.</summary>
    private const uint Near = 0x50000061u;
    private const uint Middle = 0x50000062u;
    private const uint Far = 0x50000063u;

    /// <summary>
    /// Mutation check: give the seam back to a single host's capability
    /// record, or drop the object-id tie-break out of the ordering, and the
    /// two arms record a different person at some step.
    /// </summary>
    [Fact]
    public void SteppingThroughNearbyPeopleLandsOnTheSameOnesInTheSameOrder() =>
        ParityScenario.Run((arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            StageBystanders(arm);
            ISelectionService selection = arm.Host.Selection;
            ISelectionAutomation cycle = arm.Host.Automation.Selection;
            arm.Advance();

            transcript.Step("step out from nothing selected");
            Step(transcript, cycle, selection, PluginSelectionAction.NextPlayer);
            // Both arms agreeing that nothing happened would be parity and no
            // use to anybody, so the scenario says what the right answer is.
            Assert.Equal(Near, selection.SelectedObjectId);

            transcript.Step("step out again");
            Step(transcript, cycle, selection, PluginSelectionAction.NextPlayer);
            Assert.Equal(Middle, selection.SelectedObjectId);

            transcript.Step("and again");
            Step(transcript, cycle, selection, PluginSelectionAction.NextPlayer);
            Assert.Equal(Far, selection.SelectedObjectId);

            transcript.Step("past the last one, which wraps round");
            Step(transcript, cycle, selection, PluginSelectionAction.NextPlayer);
            Assert.Equal(Near, selection.SelectedObjectId);

            transcript.Step("step back in, which wraps the other way");
            Step(
                transcript,
                cycle,
                selection,
                PluginSelectionAction.PreviousPlayer);
            Assert.Equal(Far, selection.SelectedObjectId);

            transcript.Step("back to whatever was selected before");
            Step(
                transcript,
                cycle,
                selection,
                PluginSelectionAction.PreviousSelection);
            Assert.Equal(Near, selection.SelectedObjectId);

            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// A creature is not a person: the cycle must leave the monsters the
    /// staged world is full of alone.
    /// </summary>
    [Fact]
    public void SteppingThroughPeopleIgnoresCreatures() =>
        ParityScenario.Run((arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            ISelectionService selection = arm.Host.Selection;
            ISelectionAutomation cycle = arm.Host.Automation.Selection;
            arm.Advance();

            transcript.Step("nobody to step to");
            transcript.Record(
                "executed", cycle.Execute(PluginSelectionAction.NextPlayer));
            transcript.Record("selected", selection.SelectedObjectId);
            Assert.Null(selection.SelectedObjectId);
        });

    private static void Step(
        ParityTranscript transcript,
        ISelectionAutomation cycle,
        ISelectionService selection,
        PluginSelectionAction action)
    {
        transcript.Record("executed", cycle.Execute(action));
        transcript.Record("selected", selection.SelectedObjectId);
        transcript.Record("previous", selection.PreviousObjectId);
    }

    /// <summary>
    /// Three other characters standing in a line out from the character, each
    /// shown on the radar, so the order they come out in is known in advance.
    /// </summary>
    private static void StageBystanders(ParityArm arm)
    {
        ParityWorld.Add(
            arm.Runtime, Near, ParityWorld.PlayerX + 2f, Person(Near, "Near"));
        ParityWorld.Add(
            arm.Runtime,
            Middle,
            ParityWorld.PlayerX + 5f,
            Person(Middle, "Middle"));
        ParityWorld.Add(
            arm.Runtime, Far, ParityWorld.PlayerX + 9f, Person(Far, "Far"));
    }

    private static ClientObject Person(uint objectId, string name) => new()
    {
        ObjectId = objectId,
        Type = ItemType.Creature,
        Name = name,
        PublicWeenieBitfield = SelectedObjectHealthPolicy.BfPlayer,
        RadarBehavior = (byte)RadarBehavior.ShowAlways,
    };
}
