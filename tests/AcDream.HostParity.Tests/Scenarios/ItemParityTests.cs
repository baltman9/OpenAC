using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The item commands, run against both clients. Until now one client answered
/// them through the runtime's item owner and the other through a second
/// implementation written beside it, so a plugin that used an item, salvaged,
/// sold, or waited on an equipment switch got a different answer depending on
/// which client it happened to be loaded into, and nothing compared the two.
/// Both now answer from the one owner, and these scenarios are what says so.
///
/// Mutation check (2026-09-20), run: making the shared binding pass hand one
/// arm a <c>Use</c> that always refuses turned every scenario in this file
/// red, on the status lines and on the outbound counts. Separately, stamping
/// the pacing between two uses off a wall clock on one arm instead of the
/// shared simulation clock turned the paced-use scenario and the corpse cycle
/// red. Restoring each turned them green.
/// </summary>
public sealed class ItemParityTests
{
    /// <summary>Long enough for the pacing between two uses to lapse.</summary>
    private const int TicksPastTheUsePacing = 40;

    /// <summary>Past the bound on waiting for a description, at the shared step.</summary>
    private const int TicksPastTheDescriptionWait = 400;

    [Fact]
    public void UsingACarriedItemLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            transcript.Step("before");
            RecordItemState(transcript, items);

            transcript.Step("use the kit");
            Record(transcript, "use", items.Use(ParityWorld.Kit));
            RecordItemState(transcript, items);
            transcript.RecordOutbound(arm);

            transcript.Step("the server answers");
            arm.Deliver(static runtime =>
                runtime.ActionOwner.Transactions.CompleteUse(0u));
            arm.Advance();
            RecordItemState(transcript, items);
            transcript.Record("completion.error", items.LastCompletion.WeenieError);
            transcript.Record(
                "completion.source", items.LastCompletion.SourceObjectId);
        });

    [Fact]
    public void ApplyingAnItemToATargetLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            transcript.Step("apply the stone to the kit");
            Record(
                transcript,
                "apply",
                items.Apply(ParityWorld.TargetedItem, ParityWorld.Kit));
            RecordItemState(transcript, items);
            transcript.RecordOutbound(arm);

            transcript.Step("apply it to something that is not there");
            Record(
                transcript,
                "apply",
                items.Apply(ParityWorld.TargetedItem, 0x5000_00FFu));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Refusals. The reason text matters as much as the status: a bot reads
    /// it, and the client that answered a targeted item with a bare "no" told
    /// a bot nothing it could act on.
    /// </summary>
    [Fact]
    public void RefusingAnItemLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            transcript.Step("use a targeted item with no target");
            Record(transcript, "use", items.Use(ParityWorld.TargetedItem));

            transcript.Step("use something that is not there");
            Record(transcript, "use", items.Use(0x5000_00FFu));

            transcript.Step("use a creature the player does not own");
            Record(transcript, "use", items.Use(ParityWorld.Monster));
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void MovingAnItemIntoAPackLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            transcript.Step("move the kit into the side pack");
            Record(
                transcript,
                "move",
                items.MoveToContainer(ParityWorld.Kit, ParityWorld.SidePack));
            RecordItemState(transcript, items);
            transcript.RecordOutbound(arm);

            transcript.Step("and again while the first is in flight");
            Record(
                transcript,
                "move",
                items.MoveToContainer(
                    ParityWorld.ScrapItem, ParityWorld.SidePack));
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void SalvagingAndSellingLookTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);

            transcript.Step("salvage the scrap with the tool");
            Record(
                transcript,
                "salvage",
                items.Salvage(ParityWorld.SalvageTool, [ParityWorld.ScrapItem]));
            transcript.RecordOutbound(arm);

            transcript.Step("salvage with something that is not a tool");
            Record(
                transcript,
                "salvage",
                items.Salvage(ParityWorld.Kit, [ParityWorld.ScrapItem]));

            transcript.Step("sell with no vendor trading");
            transcript.Record("vendor", items.ActiveVendorObjectId);
            Record(transcript, "sell", items.Sell(ParityWorld.ScrapItem));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Two uses close together. The second is early, not failed, and both
    /// clients have to say so off the same clock: a client stamping the
    /// pacing from a wall clock and one stamping it from the simulation clock
    /// disagree about every use that follows the first.
    /// </summary>
    [Fact]
    public void APacedUseIsReportedBusyOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = StageAndTakeItems(arm);
            ILootAutomation loot = arm.Host.Automation.Loot;
            ParityWorld.StageCorpse(arm.Runtime);

            transcript.Step("open the corpse");
            Record(transcript, "open", loot.Open(ParityWorld.Corpse));
            transcript.RecordOutbound(arm);

            transcript.Step("open it again straight away");
            Record(transcript, "open", loot.Open(ParityWorld.Corpse));
            transcript.RecordOutbound(arm);

            transcript.Step("and again once the pacing has lapsed");
            arm.Deliver(static runtime =>
                runtime.ActionOwner.Transactions.CompleteUse(0u));
            for (int step = 0; step < TicksPastTheUsePacing; step++)
                arm.Advance();
            Record(transcript, "open", loot.Open(ParityWorld.Corpse));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// The looting cycle a bot runs: open, wait for the contents, read them,
    /// look one over, take it, and try to close while the take is still in
    /// flight. Every step's busy state and container id is written down,
    /// because a bot branches on all of them.
    /// </summary>
    [Fact]
    public void ACorpseCycleLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = StageAndTakeItems(arm);
            ILootAutomation loot = arm.Host.Automation.Loot;
            ParityWorld.StageCorpse(arm.Runtime);

            transcript.Step("nothing open");
            RecordLootState(transcript, loot);
            transcript.Record("corpses", loot.CaptureCorpses(10f).Count);

            transcript.Step("open it");
            Record(transcript, "open", loot.Open(ParityWorld.Corpse));
            RecordLootState(transcript, loot);
            transcript.RecordOutbound(arm);

            transcript.Step("the contents arrive");
            arm.Deliver(static runtime =>
            {
                runtime.ActionOwner.Transactions.CompleteUse(0u);
                ParityWorld.DeliverCorpseContents(runtime);
            });
            arm.Advance();
            RecordLootState(transcript, loot);
            RecordContents(transcript, loot);

            transcript.Step("look the gem over");
            Record(transcript, "identify", loot.Identify(ParityWorld.CorpseGem));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);

            transcript.Step("the description comes back");
            arm.Deliver(static runtime =>
                runtime.ItemInteractionOwner.AcceptAppraisalResponse(
                    ParityWorld.CorpseGem));
            arm.Advance();
            RecordAppraisal(transcript, loot);

            transcript.Step("take the gem");
            for (int step = 0; step < TicksPastTheUsePacing; step++)
                arm.Advance();
            Record(transcript, "pickup", loot.Pickup(ParityWorld.CorpseGem));
            RecordLootState(transcript, loot);
            transcript.RecordOutbound(arm);

            transcript.Step("try to close while the take is out");
            for (int step = 0; step < TicksPastTheUsePacing; step++)
                arm.Advance();
            Record(transcript, "close", loot.Close(ParityWorld.Corpse));
            RecordLootState(transcript, loot);
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// A description nobody ever answers. One question is allowed out at a
    /// time, so the second is refused while the first is still out, and the
    /// wait is only let go of when somebody asks again -- not by a timer.
    /// Both clients have to hold and release the one slot at the same steps,
    /// or a bot polling for an answer stalls forever on one of them.
    ///
    /// The bound on that wait and the pacing between two uses beside it are
    /// now measured against one clock, the session's own simulation clock,
    /// so simulated waiting really does reach it: six simulated seconds of
    /// silence here end the wait, and the next question takes the slot.
    /// </summary>
    [Fact]
    public void AnUnansweredDescriptionExpiresTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IItemAutomation items = StageAndTakeItems(arm);
            IWorldObjectAutomation objects = arm.Host.Automation.Objects;
            ILootAutomation loot = arm.Host.Automation.Loot;

            transcript.Step("ask about the kit");
            Record(transcript, "identify", objects.Identify(ParityWorld.Kit));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);

            transcript.Step("ask about something else while the first is out");
            Record(transcript, "identify", objects.Identify(ParityWorld.ScrapItem));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);

            transcript.Step("nothing ever answers");
            for (int step = 0; step < TicksPastTheDescriptionWait; step++)
                arm.Advance();
            RecordAppraisal(transcript, loot);
            transcript.Record("busy", items.IsBusy);

            // The wait has run out by now, so this is the step that takes the
            // slot over -- and the one that has to take it on both clients.
            transcript.Step("ask about something else after giving up");
            Record(transcript, "identify", objects.Identify(ParityWorld.ScrapItem));
            RecordAppraisal(transcript, loot);
            transcript.RecordOutbound(arm);
            // Both arms agreeing that nothing happened would pass the
            // comparison and prove nothing, so the one slot really has to
            // have moved off the question nobody answered.
            Assert.Equal(
                ParityWorld.ScrapItem, loot.Appraisal.AwaitingObjectId);
        });

    /// <summary>
    /// Puts a character with a body and a full pack on this arm and hands
    /// back its item surface.
    /// </summary>
    private static IItemAutomation StageAndTakeItems(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        ParityWorld.StageCarriedItems(arm.Runtime);
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Items;
    }

    private static void Record(
        ParityTranscript transcript, string key, PluginItemCommandResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }

    private static void RecordItemState(
        ParityTranscript transcript, IItemAutomation items)
    {
        transcript.Record("available", items.IsAvailable);
        transcript.Record("busy", items.IsBusy);
        transcript.Record("owned", items.CaptureOwnedItems().Count);
    }

    private static void RecordLootState(
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
