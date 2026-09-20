using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The looting cycle with the server's own answers coming back off the wire:
/// open a corpse, be told what is inside, read it, ask about one of the
/// things, be told, take it, close.
///
/// The older version of this cycle poked the runtime by hand -- "pretend the
/// use completed", "pretend the listing arrived" -- which tested the owners
/// but not the road the answers travel, and could not have caught an answer
/// that never reached them. Every answer here is a message the scripted
/// server hands to each client's own world connection at the point its
/// decoder would, so the client's own inbound route, its binding records and
/// each message's own payload parser all run.
///
/// Mutation check (2026-09-20), run: stopping the description's answer from
/// reaching the channel on the way in -- leaving the listing and the
/// completion alone -- turned both description scenarios red, on the
/// awaiting and current lines and on the numbers a plugin reads back, and
/// left the opening and the pacing ones green, which is each answer
/// arriving by its own road rather than by another's. Restoring it turned
/// them green.
/// </summary>
public sealed class ContainerInboundParityTests
{
    /// <summary>Long enough for the pacing between two uses to lapse.</summary>
    private const int TicksPastTheUsePacing = 40;

    /// <summary>Past the bound on waiting for a description, at the shared step.</summary>
    private const int TicksPastTheDescriptionWait = 400;

    /// <summary>A whole-number property of a gem: how much it is worth.</summary>
    private const uint ValueProperty = 0x0013u;

    [Fact]
    public void ACorpseOpenedByTheServersOwnAnswersRunsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ILootAutomation loot = Stage(arm);

            transcript.Step("nothing open");
            RecordLoot(transcript, loot);
            transcript.Record("corpses", loot.CaptureCorpses(10f).Count);

            transcript.Step("open it");
            Record(transcript, "open", loot.Open(ParityWorld.Corpse));
            RecordLoot(transcript, loot);
            // Said outright, and before the outbound record is taken because
            // taking it empties it: the open really went out and the client
            // is waiting on this corpse. Two clients that both sent nothing
            // would agree and prove nothing.
            Assert.NotEmpty(arm.Operations.Outbound);
            Assert.Equal(ParityWorld.Corpse, loot.RequestedContainerId);
            transcript.RecordOutbound(arm);

            transcript.Step("the server lists what is inside");
            arm.Server.ViewContents(
                ParityWorld.Corpse,
                ParityWorld.CorpseCoin,
                ParityWorld.CorpseGem);
            arm.Server.UseDone();
            arm.Advance();
            RecordLoot(transcript, loot);
            RecordContents(transcript, loot);
            // The listing travelled the whole way: the corpse is open and
            // both things inside it are readable.
            Assert.Equal(ParityWorld.Corpse, loot.CurrentContainerId);
            Assert.True(loot.CurrentContentsReady);
            Assert.Equal(2, loot.CaptureCurrentContents().Count);

            transcript.Step("take the gem");
            Advance(arm, TicksPastTheUsePacing);
            Record(transcript, "pickup", loot.Pickup(ParityWorld.CorpseGem));
            RecordLoot(transcript, loot);
            transcript.RecordOutbound(arm);

            transcript.Step("the take completes");
            arm.Server.UseDone();
            arm.Advance();
            RecordLoot(transcript, loot);

            transcript.Step("close it");
            Advance(arm, TicksPastTheUsePacing);
            Record(transcript, "close", loot.Close(ParityWorld.Corpse));
            RecordLoot(transcript, loot);
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Asking what something is, and being told. The answer carries the
    /// object's whole-number properties, which is what a bot's keep-or-drop
    /// rules read, so it is not enough that the wait ends -- the numbers
    /// have to arrive.
    /// </summary>
    [Fact]
    public void ADescriptionComingBackOffTheWireReadsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ILootAutomation loot = Stage(arm);
            IWorldObjectAutomation objects = arm.Host.Automation.Objects;

            transcript.Step("ask about the gem");
            Record(transcript, "identify", objects.Identify(ParityWorld.CorpseGem));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);
            Assert.Equal(ParityWorld.CorpseGem, loot.Appraisal.AwaitingObjectId);

            transcript.Step("the server answers");
            arm.Server.AppraisalResponse(
                ParityWorld.CorpseGem,
                [(ValueProperty, 1250)]);
            arm.Advance();
            RecordAppraisal(transcript, loot);
            transcript.Record(
                "value",
                objects.TryGetIntProperty(
                    ParityWorld.CorpseGem, ValueProperty, out int value)
                    ? value
                    : -1);
            // The answer really arrived and really carried its numbers: the
            // wait is over, the channel names this object, and what the
            // server said is readable through the plugin surface.
            Assert.Equal(0u, loot.Appraisal.AwaitingObjectId);
            Assert.Equal(ParityWorld.CorpseGem, loot.Appraisal.CurrentObjectId);
            Assert.True(
                objects.TryGetIntProperty(
                    ParityWorld.CorpseGem, ValueProperty, out int told));
            Assert.Equal(1250, told);

            transcript.Step("ask about something else");
            Record(transcript, "identify", objects.Identify(ParityWorld.CorpseCoin));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);
            Assert.Equal(ParityWorld.CorpseCoin, loot.Appraisal.AwaitingObjectId);
        });

    /// <summary>
    /// A description nobody ever answers. One question is out at a time, so
    /// the second is refused while the first is still out; the wait is let go
    /// of on the simulation clock, and the next question then takes the
    /// channel. A client whose clock never reaches the bound leaves a bot
    /// polling forever.
    /// </summary>
    [Fact]
    public void AnUnansweredDescriptionFreesTheChannelTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ILootAutomation loot = Stage(arm);
            IWorldObjectAutomation objects = arm.Host.Automation.Objects;

            transcript.Step("ask about the gem");
            Record(transcript, "identify", objects.Identify(ParityWorld.CorpseGem));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);

            transcript.Step("ask again while the first is out");
            Record(transcript, "identify", objects.Identify(ParityWorld.CorpseCoin));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);
            Assert.Equal(ParityWorld.CorpseGem, loot.Appraisal.AwaitingObjectId);

            transcript.Step("nothing ever answers");
            Advance(arm, TicksPastTheDescriptionWait);
            RecordAppraisal(transcript, loot);

            transcript.Step("ask again after giving up");
            Record(transcript, "identify", objects.Identify(ParityWorld.CorpseCoin));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);
            // The channel really moved on to the new question rather than
            // staying stuck on the one nobody answered.
            Assert.Equal(ParityWorld.CorpseCoin, loot.Appraisal.AwaitingObjectId);

            transcript.Step("and that one is answered");
            arm.Server.AppraisalResponse(
                ParityWorld.CorpseCoin,
                [(ValueProperty, 250)]);
            arm.Advance();
            RecordAppraisal(transcript, loot);
            Assert.Equal(ParityWorld.CorpseCoin, loot.Appraisal.CurrentObjectId);
        });

    /// <summary>
    /// Two opens close together. The second is early rather than failed, and
    /// both clients have to say so off the same clock: a client stamping the
    /// pacing from a wall clock and one stamping it from the simulation clock
    /// disagree about every use that follows the first.
    /// </summary>
    [Fact]
    public void AnOpenInsideThePacingIsBusyThenStartedOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ILootAutomation loot = Stage(arm);

            transcript.Step("open it");
            Record(transcript, "open", loot.Open(ParityWorld.Corpse));
            transcript.RecordOutbound(arm);

            transcript.Step("open it again straight away");
            PluginItemCommandResult early = loot.Open(ParityWorld.Corpse);
            Record(transcript, "open", early);
            transcript.RecordOutbound(arm);
            // Early, not failed: a bot that reads this as a failure spends
            // one of the corpse's attempts on a fifth of a second's wait.
            Assert.Equal(PluginItemCommandStatus.Busy, early.Status);

            transcript.Step("and again once the pacing has lapsed");
            arm.Server.UseDone();
            Advance(arm, TicksPastTheUsePacing);
            PluginItemCommandResult later = loot.Open(ParityWorld.Corpse);
            Record(transcript, "open", later);
            transcript.RecordOutbound(arm);
            Assert.Equal(PluginItemCommandStatus.Started, later.Status);
        });

    /// <summary>
    /// A character with a body and a full pack, a corpse on the ground with
    /// two things in it that the client knows about but has not been told
    /// are there, and nothing sent yet.
    ///
    /// The corpse lies at arm's length here. These scenarios are about the
    /// answers that come back off the wire and about the pacing between two
    /// opens; an open of something out of reach walks to it first, and the
    /// walk belongs to the scenarios that are about walking.
    /// </summary>
    private static ILootAutomation Stage(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        ParityWorld.StageCarriedItems(arm.Runtime);
        ParityWorld.StageCorpse(
            arm.Runtime,
            metresOut: ParityWorld.WithinArmsReach);
        ParityWorld.StageCorpseContents(arm.Runtime);
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Loot;
    }

    private static void Advance(ParityArm arm, int ticks)
    {
        for (int step = 0; step < ticks; step++)
            arm.Advance();
    }

    private static void Record(
        ParityTranscript transcript, string key, PluginItemCommandResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }

    private static void RecordLoot(
        ParityTranscript transcript, ILootAutomation loot)
    {
        transcript.Record("available", loot.IsAvailable);
        transcript.Record("busy", loot.IsBusy);
        transcript.Record("requested", loot.RequestedContainerId);
        transcript.Record("current", loot.CurrentContainerId);
        transcript.Record("ready", loot.CurrentContentsReady);
    }

    private static void RecordContents(
        ParityTranscript transcript, ILootAutomation loot)
    {
        IReadOnlyList<PluginInventoryItem> contents =
            loot.CaptureCurrentContents();
        transcript.Record("contents.count", contents.Count);
        for (int index = 0; index < contents.Count; index++)
        {
            transcript.Record($"contents[{index}].id", contents[index].ObjectId);
            transcript.Record($"contents[{index}].name", contents[index].Name);
        }
    }

    private static void RecordAppraisal(
        ParityTranscript transcript, ILootAutomation loot)
    {
        PluginAppraisalState appraisal = loot.Appraisal;
        transcript.Record("appraisal.awaiting", appraisal.AwaitingObjectId);
        transcript.Record("appraisal.current", appraisal.CurrentObjectId);
        transcript.Record("appraisal.abandoned", appraisal.LastAbandonedObjectId);
    }
}
