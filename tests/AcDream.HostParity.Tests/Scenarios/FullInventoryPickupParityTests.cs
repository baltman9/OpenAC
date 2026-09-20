using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Taking loot into an inventory with no free slot left, on both clients.
/// Adding to a stack the character already carries costs no slot, and a
/// placement that looked for room before it looked for that stack refused
/// the take on both -- so a bot looting coins was stopped by a full pack it
/// never needed a slot in.
///
/// Regression guard rather than parity evidence: the placement lives in the
/// runtime and both arms run it, so agreement here is true by construction.
/// It is worth keeping because it fails if either client grows a placement of
/// its own again, and because the assertions inside it pin the answer rather
/// than only the agreement -- both arms refusing would match and say nothing.
///
/// Mutation (run): putting the room search back in front of the join turned
/// <see cref="TakingWhatJoinsACarriedStackLooksTheSameOnBothClients"/> red on
/// both arms.
/// </summary>
public sealed class FullInventoryPickupParityTests
{
    /// <summary>Long enough for the pacing between two uses to lapse.</summary>
    private const int TicksPastTheUsePacing = 40;

    [Fact]
    public void TakingWhatJoinsACarriedStackLooksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ILootAutomation loot = OpenTheCorpseWithNoRoomLeft(arm, transcript);

            transcript.Step("take coins that join the carried stack");
            PluginItemCommandResult coins = loot.Pickup(ParityWorld.CorpseCoin);
            Record(transcript, "pickup", coins);
            transcript.RecordOutbound(arm);

            // Both arms refusing would compare equal and prove nothing, so
            // the take really has to have gone out.
            Assert.Equal(PluginItemCommandStatus.Started, coins.Status);
        });

    [Fact]
    public void TakingWhatNeedsASlotIsRefusedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ILootAutomation loot = OpenTheCorpseWithNoRoomLeft(arm, transcript);

            transcript.Step("take a gem, which needs a slot of its own");
            PluginItemCommandResult gem = loot.Pickup(ParityWorld.CorpseGem);
            Record(transcript, "pickup", gem);
            transcript.RecordOutbound(arm);

            // The other half of the pair: the inventory really is full, so a
            // take that needs a slot is still refused and says why.
            Assert.Equal(PluginItemCommandStatus.Refused, gem.Status);
            Assert.False(string.IsNullOrWhiteSpace(gem.Notice));
        });

    /// <summary>
    /// Stages a character with nothing but a part-filled coin stack to add
    /// to, opens the corpse at its feet and waits out the pacing, so the next
    /// thing the scenario does is the take itself.
    /// </summary>
    private static ILootAutomation OpenTheCorpseWithNoRoomLeft(
        ParityArm arm,
        ParityTranscript transcript)
    {
        _ = ParityWorld.Stage(arm);
        ParityWorld.StageAFullInventoryOverAStack(arm.Runtime);
        ParityWorld.StageCorpse(arm.Runtime, ParityWorld.WithinArmsReach);
        ILootAutomation loot = arm.Host.Automation.Loot;
        _ = arm.Operations.TakeOutbound();

        transcript.Step("open the corpse");
        Record(transcript, "open", loot.Open(ParityWorld.Corpse));
        transcript.RecordOutbound(arm);

        transcript.Step("the contents arrive");
        arm.Deliver(static runtime =>
        {
            runtime.ActionOwner.Transactions.CompleteUse(0u);
            ParityWorld.DeliverCorpseContents(runtime);
        });
        arm.Advance();
        transcript.Record("ready", loot.CurrentContentsReady);
        transcript.Record("contents", loot.CaptureCurrentContents().Count);

        for (int step = 0; step < TicksPastTheUsePacing; step++)
            arm.Advance();
        return loot;
    }

    private static void Record(
        ParityTranscript transcript, string key, PluginItemCommandResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }
}
