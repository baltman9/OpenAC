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
/// The whole walk is under test here: the arming, the silence while the
/// character is under way, the use that goes out on arrival and the give-up on
/// a walk that never arrives. All four are one runtime owner driven once a
/// frame from the per-frame local-player step, so they happen with or without
/// a window. The last two used to be driven only by the client that draws, so
/// a client with nothing to draw on walked to the corpse and stood there, and
/// a stalled walk held the one-request-at-a-time gate for the rest of the
/// session.
///
/// Mutation checks (2026-09-20), both run: making the shared route send the
/// use immediately instead of arming it for arrival turned two scenarios red
/// -- no walk installed, and a use among what was sent before the character
/// had got anywhere. Taking the per-frame drive back out turned the arrival
/// and the give-up red on both clients. Restoring each turned them green.
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
    /// Long enough for a walk that gets nowhere to be given up on: the route
    /// waits out a floor of about five seconds of standstill, plus the grace
    /// the walk itself gives before it counts a standstill at all.
    /// </summary>
    private const int TicksUntilTheWalkIsGivenUpOn = 1200;

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
    /// The other half of the same walk: once the character is there, the use
    /// goes out and the client starts waiting for the corpse's contents. This
    /// is what used to happen only where there was something drawing the
    /// world -- a client with no window walked to the corpse and stood there.
    /// </summary>
    [Fact]
    public void ArrivingSendsTheArmedUseOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ILootAutomation loot = Stage(arm);
            IItemAutomation items = arm.Host.Automation.Items;

            transcript.Step("ask to use a corpse three metres off");
            Record(transcript, "use", items.Use(ParityWorld.Corpse));
            AssertNoUseWasSent(arm);
            transcript.RecordOutbound(arm);

            transcript.Step("the character arrives");
            Advance(arm, TicksForTheWholeWalk);
            RecordWalk(transcript, arm);
            RecordLoot(transcript, loot);
            transcript.Record("distance", Distance(arm, ParityWorld.Corpse));
            // Said outright, because two clients that both sent nothing agree
            // line for line: the use really went out, and the client is now
            // waiting on this corpse rather than on nothing.
            AssertTheUseWasSent(arm);
            Assert.Equal(ParityWorld.Corpse, loot.RequestedContainerId);
            transcript.RecordOutbound(arm);

            transcript.Step("the corpse answers");
            arm.Server.UseDone();
            ParityWorld.DeliverCorpseContents(arm.Runtime);
            arm.Advance();
            RecordLoot(transcript, loot);
            transcript.Record("busy", items.IsBusy);
        });

    /// <summary>
    /// A walk that can never arrive: the corpse is on a ledge four metres up.
    /// The route gives up on it, stops the character walking into the wall
    /// under it, and lets the next request through -- on both clients. Without
    /// the give-up the one-request-at-a-time gate stayed held and every later
    /// use in the session answered busy.
    /// </summary>
    [Fact]
    public void AWalkThatNeverArrivesIsGivenUpOnOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = Stage(arm);
            ParityWorld.StageCorpseOutOfEveryReach(arm.Runtime);
            IItemAutomation items = arm.Host.Automation.Items;

            transcript.Step("ask to use the corpse on the ledge");
            Record(
                transcript,
                "use",
                items.Use(ParityWorld.CorpseOutOfEveryReach));
            RecordWalk(transcript, arm);
            Assert.True(Walk(arm).IsMovingTo());
            transcript.RecordOutbound(arm);

            transcript.Step("the walk stops getting anywhere");
            Advance(arm, TicksUntilTheWalkIsGivenUpOn);
            RecordWalk(transcript, arm);
            transcript.Record("busy", items.IsBusy);
            // Said outright: the walk was called off, and nothing was sent
            // for a use whose walk never arrived.
            Assert.False(
                Walk(arm).IsMovingTo(),
                "The character is still walking at something it cannot reach.");
            AssertNoUseWasSent(arm);
            transcript.RecordOutbound(arm);

            transcript.Step("the next request is let through");
            Advance(arm, TicksPastTheUsePacing);
            PluginItemCommandResult again = items.Use(ParityWorld.Corpse);
            Record(transcript, "use", again);
            RecordWalk(transcript, arm);
            // The gate is free again: this is the request that answered busy
            // for the rest of the session before the give-up existed.
            Assert.Equal(PluginItemCommandStatus.Started, again.Status);
            transcript.RecordOutbound(arm);
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
        IReadOnlyList<ParityOutbound> sent = arm.Operations.Outbound;
        Assert.DoesNotContain(
            UseAction,
            sent.Select(static message => message.GameAction ?? 0u));
    }

    /// <summary>
    /// That a use HAS gone out. The counterpart of the above, and the one a
    /// scenario about arriving needs: without it both clients standing still
    /// would agree.
    /// </summary>
    private static void AssertTheUseWasSent(ParityArm arm)
    {
        IReadOnlyList<ParityOutbound> sent = arm.Operations.Outbound;
        Assert.Contains(
            UseAction,
            sent.Select(static message => message.GameAction ?? 0u));
    }

    /// <summary>The client action that says "use that".</summary>
    private const uint UseAction = 0x0036u;

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
