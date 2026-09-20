using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The walk a plugin gets for free when it asks to use something out of
/// arm's reach: a corpse lying three metres off, on both clients.
///
/// The walk-then-use route is one runtime class both clients build, but until
/// now nothing had ever WALKED it under a test. The older scenarios asked to
/// open a corpse and compared the answers, and every one of those answers
/// was a refusal on both clients -- the connection under the harness had
/// never been told the character was in the world, so every use, pickup and
/// description was turned away before it was composed. Two clients that both
/// refuse agree line for line. Here the character really covers the ground.
///
/// A gap these scenarios found and deliberately stop short of: the step that
/// SENDS the use once the walk has arrived, and the one that gives up on a
/// walk going nowhere, are driven only by the windowed client's own
/// interaction controller, which cannot exist without a presentation tree.
/// Nothing in the runtime drives either. So a client with no window walks to
/// the corpse and then stands there: the use is never sent, and a walk that
/// stalls leaves the one-request-at-a-time gate held for the rest of the
/// session. Neither is claimed below, because neither happens here; both are
/// reported, and the arming, the walk and the silence before arrival are
/// pinned so that the fix has something to turn green.
///
/// Mutation check (2026-09-20), run: making the shared route send the use
/// immediately instead of arming it for arrival turned two of the three red
/// -- no walk installed, and a use among what was sent before the character
/// had got anywhere. Restoring it turned them green.
/// </summary>
public sealed class WalkThenUseParityTests
{
    /// <summary>Roughly a fifth of a second at the shared step length.</summary>
    private const int TicksForTwoHundredMilliseconds = 13;

    /// <summary>Long enough for a walk of a few metres to finish.</summary>
    private const int TicksForTheWholeWalk = 400;

    /// <summary>Long enough for the pacing between two uses to lapse.</summary>
    private const int TicksPastTheUsePacing = 40;

    /// <summary>
    /// Asking to use a corpse three metres off: the walk is armed and aimed
    /// at the corpse itself, nothing is sent while the character is under
    /// way, and it ends up within reach.
    /// </summary>
    [Fact]
    public void AskingToUseACorpseOutOfReachWalksThereOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ILootAutomation loot = Stage(arm);
            IItemAutomation items = arm.Host.Automation.Items;

            transcript.Step("ask to use a corpse three metres off");
            transcript.Record("body", arm.HasLiveBody);
            transcript.Record("distance", Distance(arm, ParityWorld.Corpse));
            Record(transcript, "use", items.Use(ParityWorld.Corpse));
            RecordWalk(transcript, arm);
            RecordLoot(transcript, loot);
            // Said outright, and said BEFORE the outbound record is taken,
            // because taking it empties it: a walk was begun, aimed at the
            // corpse, and the use was NOT sent. A client that sent it from
            // out of reach gets a walk order back and then, much later, a
            // bare "done" with nothing done.
            Assert.Equal(MovementType.MoveToObject, Walk(arm).MovementTypeState);
            Assert.Equal(ParityWorld.Corpse, Walk(arm).SoughtObjectId);
            AssertNoUseWasSent(arm);
            transcript.RecordOutbound(arm);

            transcript.Step("half way");
            Advance(arm, TicksForTwoHundredMilliseconds);
            RecordWalk(transcript, arm);
            transcript.Record("distance", Distance(arm, ParityWorld.Corpse));
            Assert.True(Walk(arm).IsMovingTo());

            transcript.Step("the character arrives");
            Advance(arm, TicksForTheWholeWalk);
            RecordWalk(transcript, arm);
            transcript.Record("distance", Distance(arm, ParityWorld.Corpse));
            transcript.RecordOutbound(arm);
            // The walk really got there rather than giving up part way.
            Assert.False(Walk(arm).IsMovingTo());
            Assert.True(
                Distance(arm, ParityWorld.Corpse) < 1d,
                $"The walk stopped {Distance(arm, ParityWorld.Corpse):0.00} m "
                + "short of the corpse.");
        });

    /// <summary>
    /// The walk is called off half way. Nothing may be sent for a use whose
    /// walk never arrived.
    /// </summary>
    [Fact]
    public void AWalkCalledOffHalfWayLeavesNoUseOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ILootAutomation loot = Stage(arm);
            IItemAutomation items = arm.Host.Automation.Items;
            INavigationAutomation navigation = arm.Host.Automation.Navigation;

            transcript.Step("ask to use it");
            Record(transcript, "use", items.Use(ParityWorld.Corpse));
            Advance(arm, TicksForTwoHundredMilliseconds);
            RecordWalk(transcript, arm);
            // Half way there: the walk is running and nothing has been sent.
            // Asked before the outbound record is taken, which empties it.
            Assert.True(Walk(arm).IsMovingTo());
            AssertNoUseWasSent(arm);
            transcript.RecordOutbound(arm);

            transcript.Step("the plugin takes the character back");
            transcript.Record(
                "plugin.status",
                navigation.SetMovementIntent(
                    new PluginMovementIntent(Backward: true)));
            Advance(arm, TicksForTwoHundredMilliseconds);
            RecordWalk(transcript, arm);
            RecordLoot(transcript, loot);
            transcript.RecordOutbound(arm);
            Assert.False(
                Walk(arm).IsMovingTo(),
                "The walk survived the plugin taking the character back.");

            transcript.Step("let go and ask again");
            transcript.Record("plugin.status", navigation.ClearMovementIntent());
            transcript.Record("plugin.stop", navigation.StopMoving());
            Advance(arm, TicksPastTheUsePacing);
            Record(transcript, "use", items.Use(ParityWorld.Corpse));
            RecordWalk(transcript, arm);
            RecordLoot(transcript, loot);
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Using something the character owns on something it does not: the
    /// stone in its pack on the corpse on the ground. Seen from the other
    /// side of the same route.
    /// </summary>
    [Fact]
    public void ApplyingAnOwnedItemToAWorldObjectRunsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = Stage(arm);
            IItemAutomation items = arm.Host.Automation.Items;

            transcript.Step("apply the stone to the corpse");
            Record(
                transcript,
                "apply",
                items.Apply(ParityWorld.TargetedItem, ParityWorld.Corpse));
            transcript.Record("busy", items.IsBusy);
            RecordWalk(transcript, arm);
            transcript.RecordOutbound(arm);

            transcript.Step("a few frames on");
            Advance(arm, TicksForTheWholeWalk);
            transcript.Record("busy", items.IsBusy);
            RecordWalk(transcript, arm);
            transcript.RecordOutbound(arm);

            transcript.Step("the server answers");
            arm.Server.UseDone();
            arm.Advance();
            transcript.Record("busy", items.IsBusy);
            transcript.Record(
                "completion.error", items.LastCompletion.WeenieError);
        });

    /// <summary>
    /// A character with a body, a full pack and a corpse on the ground three
    /// metres out, with nothing sent yet.
    /// </summary>
    private static ILootAutomation Stage(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        ParityWorld.StageCarriedItems(arm.Runtime);
        ParityWorld.StageCorpse(arm.Runtime);
        ParityWorld.StageCorpseContents(arm.Runtime);
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Loot;
    }

    /// <summary>
    /// That no use has gone out. Counting messages will not do it: a walking
    /// character is telling the server where it is the whole time, so this
    /// looks for the client action itself.
    /// </summary>
    private static void AssertNoUseWasSent(ParityArm arm)
    {
        const uint UseAction = 0x0036u;
        IReadOnlyList<ParityOutbound> sent = arm.Operations.Outbound;
        Assert.DoesNotContain(
            UseAction,
            sent.Select(static message => message.GameAction ?? 0u));
    }

    private static void Advance(ParityArm arm, int ticks)
    {
        for (int step = 0; step < ticks; step++)
            arm.Advance();
    }

    private static MoveToManager Walk(ParityArm arm) =>
        arm.Runtime.MovementOwner.Controller?.MoveTo
        ?? throw new InvalidOperationException(
            "The character has no walk manager, so nothing can walk anywhere.");

    private static void RecordWalk(ParityTranscript transcript, ParityArm arm)
    {
        MoveToManager walk = Walk(arm);
        transcript.Record("walk.installed", walk.MovementTypeState.ToString());
        transcript.Record("walk.moving", walk.IsMovingTo());
        transcript.Record("walk.sought", walk.SoughtObjectId);
        transcript.Record("walk.stalled", walk.FailProgressCount);
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

    private static double Distance(ParityArm arm, uint objectId)
    {
        INavigationAutomation navigation = arm.Host.Automation.Navigation;
        return navigation.TryGetObject(objectId, out PluginNavigationObject value)
            ? navigation.Snapshot.Position.HorizontalDistanceMeters(
                value.Position)
            : double.NaN;
    }

    private static void Record(
        ParityTranscript transcript, string key, PluginItemCommandResult result)
    {
        transcript.Record($"{key}.status", result.Status.ToString());
        transcript.Record($"{key}.notice", result.Notice);
    }
}
