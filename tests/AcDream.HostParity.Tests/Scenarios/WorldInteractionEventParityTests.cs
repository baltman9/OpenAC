using AcDream.Plugin.Abstractions;
using AcDream.Runtime;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The three world-interaction events a plugin can wait on: a use it started
/// finishing, the character going through a portal, and an activation on a
/// world object ending. Each arrives from one runtime owner and is delivered
/// by each client's own event sink, so the way for them to go wrong is for
/// one client's sink to stamp or drop differently from the other's.
///
/// Every one is asserted outright as well as recorded, because two clients
/// that both delivered nothing would write identical transcripts.
///
/// Mutation check (2026-09-21), run: making the windowless client's portal
/// delivery a no-op turned
/// <see cref="GoingThroughAPortalTellsAPluginOnBothClients"/> red on that
/// arm alone; making its use-completion and activation-completion deliveries
/// no-ops turned the other two red, one each. Restoring all three turned
/// them green.
/// </summary>
public sealed class WorldInteractionEventParityTests
{
    [Fact]
    public void AUseFinishingTellsAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ParityWorld.StageCorpse(arm.Runtime, ParityWorld.WithinArmsReach);
            var completions = new List<PluginItemUseCompletion>();
            arm.Host.Events.ItemUseCompleted += completions.Add;

            transcript.Step("use the corpse");
            Assert.Equal(
                PluginItemCommandStatus.Started,
                arm.Host.Automation.Items.Use(ParityWorld.Corpse).Status);

            transcript.Step("the server says the use is done");
            arm.Server.UseDone();
            arm.Advance();
            transcript.Record("completions", completions.Count);
            for (int index = 0; index < completions.Count; index++)
            {
                transcript.Record(
                    $"completion[{index}].revision", completions[index].Revision);
                transcript.Record(
                    $"completion[{index}].error", completions[index].WeenieError);
            }

            // Exactly once, and with no error: a plugin that is told twice
            // takes its next step twice.
            Assert.Single(completions);
            Assert.Equal(0u, completions[0].WeenieError);
        });

    [Fact]
    public void GoingThroughAPortalTellsAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var transitions = new List<PluginPortalTransition>();
            arm.Host.Events.PortalTransition += transitions.Add;
            RuntimePortalSnapshot leaving = RuntimePortalSnapshot.Idle with
            {
                Generation = 4,
                Kind = RuntimePortalKind.Portal,
            };

            transcript.Step("the character goes into a portal");
            arm.Runtime.EventSink.EmitPortal(leaving);
            // The same state again: a plugin must not hear the transition
            // twice because the runtime republished an unchanged snapshot.
            arm.Runtime.EventSink.EmitPortal(leaving);
            Record(transcript, "leaving", transitions);
            Assert.Single(transitions);
            Assert.Equal(PluginPortalTransitionKind.Portal, transitions[0].Kind);
            Assert.Equal(1L, transitions[0].Revision);

            transcript.Step("and comes out the other side");
            arm.Runtime.EventSink.EmitPortal(leaving with { Completed = true });
            Record(transcript, "arrived", transitions);
            Assert.Equal(2, transitions.Count);
            Assert.True(transitions[1].IsCompleted);
            // Stamped in order by the client's own sink, so a plugin can tell
            // which transition came first on either client.
            Assert.Equal(2L, transitions[1].Revision);
        });

    [Fact]
    public void AnActivationEndingTellsAPluginOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ParityWorld.StageCorpse(arm.Runtime, ParityWorld.WithinArmsReach);
            var endings = new List<PluginActivationCompletion>();
            arm.Host.Events.ActivationCompleted += endings.Add;

            transcript.Step("activate the corpse");
            transcript.Record(
                "activate.status",
                arm.Host.Automation.Objects.Activate(
                    ParityWorld.Corpse).Status.ToString());
            transcript.Record("endings", endings.Count);
            // Nothing has ended yet.
            Assert.Empty(endings);

            transcript.Step("a portal completes while it is outstanding");
            arm.Runtime.EventSink.EmitPortal(RuntimePortalSnapshot.Idle with
            {
                Generation = 7,
                Kind = RuntimePortalKind.Portal,
                Completed = true,
            });
            transcript.Record("endings", endings.Count);
            for (int index = 0; index < endings.Count; index++)
            {
                transcript.Record($"ending[{index}].id", endings[index].ObjectId);
                transcript.Record(
                    $"ending[{index}].outcome", endings[index].Outcome.ToString());
            }

            Assert.Single(endings);
            Assert.Equal(ParityWorld.Corpse, endings[0].ObjectId);
            Assert.Equal(PluginActivationOutcome.Completed, endings[0].Outcome);
            Assert.True(endings[0].IsSuccess);
        });

    private static void Record(
        ParityTranscript transcript,
        string key,
        IReadOnlyList<PluginPortalTransition> transitions)
    {
        transcript.Record($"{key}.count", transitions.Count);
        for (int index = 0; index < transitions.Count; index++)
        {
            transcript.Record(
                $"{key}[{index}].revision", transitions[index].Revision);
            transcript.Record(
                $"{key}[{index}].kind", transitions[index].Kind.ToString());
            transcript.Record(
                $"{key}[{index}].completed", transitions[index].IsCompleted);
        }
    }
}
