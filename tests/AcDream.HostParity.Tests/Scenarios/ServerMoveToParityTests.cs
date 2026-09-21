using AcDream.Core.Physics;
using AcDream.Core.Physics.Motion;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The server telling the character to walk somewhere, on both clients.
///
/// Why it matters: ask the server to use something out of arm's reach and it
/// does not refuse. It sends the character to it and waits. A client that
/// lets that order pass stands still until the server gives up and answers
/// "done" with nothing done -- and a corpse a couple of metres off is the
/// ordinary case after a fight. A client with a window has always obeyed
/// these orders. These scenarios are what says the other one does too, and
/// that both walk the same distance in the same number of steps and tell the
/// server the same thing on the way.
///
/// The order arrives the way the server sends it, through each arm's own
/// world connection and its own inbound route; nothing is poked into the
/// runtime by hand.
///
/// Mutation check (2026-09-20), run: making the inbound route drop a
/// server-driven movement aimed at the character -- what a client without the
/// shared route used to do -- turned every scenario in this file red on the
/// "installed", "sought" and distance lines. Restoring it turned them green.
/// </summary>
public sealed class ServerMoveToParityTests
{
    /// <summary>Roughly a fifth of a second at the shared step length.</summary>
    private const int TicksForTwoHundredMilliseconds = 13;

    /// <summary>Long enough for a walk of a few metres to finish.</summary>
    private const int TicksForTheWholeWalk = 400;

    /// <summary>How close the order tells the character to get, in metres.</summary>
    private const float OrderedDistance = 1.5f;

    [Fact]
    public void AnOrderToWalkToACreatureIsObeyedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            INavigationAutomation navigation = Stage(arm);
            double startedAt = Distance(navigation, ParityWorld.SecondMonster);

            transcript.Step("the order arrives");
            arm.Server.MoveToObject(
                ParityWorld.Player,
                ParityWorld.SecondMonster,
                OrderedDistance,
                ParityWorld.PlayerX + 7f,
                ParityWorld.PlayerY);
            RecordOrder(transcript, arm);
            // Said outright: the order has to be installed as a walk to the
            // THING, not to the spot it stood on when the order was written,
            // or a target that moves is walked to the wrong place.
            Assert.Equal(MovementType.MoveToObject, MoveTo(arm).MovementTypeState);
            Assert.Equal(ParityWorld.SecondMonster, MoveTo(arm).SoughtObjectId);

            transcript.Step("a fifth of a second in");
            Advance(arm, TicksForTwoHundredMilliseconds);
            RecordOrder(transcript, arm);
            RecordWhere(transcript, navigation);
            transcript.RecordOutbound(arm);

            transcript.Step("the rest of the way");
            Advance(arm, TicksForTheWholeWalk);
            RecordOrder(transcript, arm);
            RecordWhere(transcript, navigation);
            transcript.RecordOutbound(arm);

            // Both clients standing still would agree line for line and prove
            // nothing, so insist that the character really closed the gap and
            // that the walk really ended.
            double ended = Distance(navigation, ParityWorld.SecondMonster);
            Assert.True(
                ended < startedAt - 1d,
                $"The character did not close the gap: {startedAt:0.00} m to "
                + $"{ended:0.00} m.");
            Assert.True(
                ended <= OrderedDistance + 1d,
                $"The walk stopped {ended:0.00} m off, further than the "
                + $"{OrderedDistance:0.00} m the order asked for.");
            Assert.False(MoveTo(arm).IsMovingTo());
        });

    // A difference found here and NOT closed, written down so it is not lost:
    // when the server says a creature the walk is aimed at has moved, a
    // client with no window re-aims the walk at where it went and a client
    // with a window keeps walking to where the creature was. Measured at the
    // shared step: the aim reads 108 m against 103 m from the fourth tenth of
    // a second on, the character ends 1.2 m from the creature against 6.4 m,
    // and the two tell the server a different number of positions on the way.
    //
    // Traced (2026-09-20). A walk aimed at a thing is re-aimed by that
    // THING's own per-frame step -- its target manager tells everyone
    // watching it where it has got to, and the walker takes that as its new
    // aim. Each client runs that step from somewhere else:
    // * with a window, from the per-frame advance of the creature's own body,
    //   beside its animation; that advance is part of carrying a remote body
    //   between the server's updates, and it is absent here because nothing
    //   is drawn and no remote body is being carried;
    // * with no window, from the character's own frame, which asks the
    //   runtime to run the step for whatever its walk is aimed at -- exactly
    //   because nothing else there would.
    // So it is one retail step with two drivers, not a step one client is
    // missing, and closing it means the runtime carrying remote bodies for
    // both clients. No scenario asserts it until then: a comparison either
    // client can pass by standing still is worth nothing.

    /// <summary>
    /// A plugin holding a movement key while the server's walk is running.
    /// The plugin's own intent wins -- the character is the player's again --
    /// and both clients have to hand it over at the same step.
    /// </summary>
    [Fact]
    public void APluginTakingTheCharacterBackEndsTheOrderTheSameWay() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            INavigationAutomation navigation = Stage(arm);

            transcript.Step("the order arrives");
            arm.Server.MoveToObject(
                ParityWorld.Player,
                ParityWorld.SecondMonster,
                OrderedDistance,
                ParityWorld.PlayerX + 7f,
                ParityWorld.PlayerY);
            Advance(arm, TicksForTwoHundredMilliseconds);
            RecordOrder(transcript, arm);
            // The walk really is running, so calling it off means something.
            Assert.True(MoveTo(arm).IsMovingTo());

            transcript.Step("the plugin holds a key");
            transcript.Record(
                "plugin.status",
                navigation.SetMovementIntent(
                    new PluginMovementIntent(Forward: true, Run: true)));
            Advance(arm, TicksForTwoHundredMilliseconds);
            RecordOrder(transcript, arm);
            RecordWhere(transcript, navigation);
            transcript.RecordOutbound(arm);
            Assert.False(
                MoveTo(arm).IsMovingTo(),
                "The server's walk survived a plugin taking the character "
                + "back.");

            transcript.Step("let go");
            transcript.Record("plugin.status", navigation.ClearMovementIntent());
            transcript.Record("plugin.stop", navigation.StopMoving());
            Advance(arm, TicksForTwoHundredMilliseconds);
            RecordOrder(transcript, arm);
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// The whole shape of a swing from out of range: the plugin asks, the
    /// server answers with a walk rather than a refusal, the character closes
    /// the distance, and the swing then resolves.
    /// </summary>
    [Fact]
    public void ASwingFromOutOfRangeWalksInAndResolvesTheSameWay() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            INavigationAutomation navigation = Stage(arm);
            ICombatAutomation combat = arm.Host.Automation.Combat;
            _ = combat.EnterMode(PluginCombatMode.Melee);
            arm.Advance();
            _ = arm.Operations.TakeOutbound();

            transcript.Step("swing at something six metres off");
            transcript.Record(
                "distance", Distance(navigation, ParityWorld.SecondMonster));
            PluginCombatCommandResult begun = combat.BeginPhysicalAttack(
                ParityWorld.SecondMonster, PluginAttackHeight.Medium, 0.75f);
            transcript.Record("begin.status", begun.Status);
            transcript.Record("begin.notice", begun.Notice);
            PluginCombatCommandResult released = combat.ReleasePhysicalAttack();
            transcript.Record("release.status", released.Status);
            arm.Advance();
            transcript.RecordOutbound(arm);

            transcript.Step("the server sends the character in");
            arm.Server.MoveToObject(
                ParityWorld.Player,
                ParityWorld.SecondMonster,
                OrderedDistance,
                ParityWorld.PlayerX + 7f,
                ParityWorld.PlayerY);
            RecordOrder(transcript, arm);
            Assert.Equal(MovementType.MoveToObject, MoveTo(arm).MovementTypeState);

            transcript.Step("it arrives");
            Advance(arm, TicksForTheWholeWalk);
            RecordOrder(transcript, arm);
            RecordWhere(transcript, navigation);
            transcript.RecordOutbound(arm);
            Assert.True(
                Distance(navigation, ParityWorld.SecondMonster)
                    <= OrderedDistance + 1d,
                "The swing's walk never got the character into range.");

            transcript.Step("the swing lands");
            arm.Server.UpdateHealth(ParityWorld.SecondMonster, 0.4f);
            arm.Advance();
            transcript.Record(
                "health",
                arm.Runtime.ActionOwner.Combat.GetHealthPercent(
                    ParityWorld.SecondMonster));
            transcript.Record("snapshot.mode", combat.Snapshot.Mode);
            transcript.RecordOutbound(arm);
            Assert.True(
                arm.Runtime.ActionOwner.Combat.HasHealth(
                    ParityWorld.SecondMonster));
        });


    /// <summary>
    /// A character with a body, a creature six metres out and a clean
    /// outbound record.
    /// </summary>
    private static INavigationAutomation Stage(ParityArm arm)
    {
        _ = ParityWorld.Stage(arm);
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Navigation;
    }

    private static void Advance(ParityArm arm, int ticks)
    {
        for (int step = 0; step < ticks; step++)
            arm.Advance();
    }

    private static MoveToManager MoveTo(ParityArm arm) =>
        arm.Runtime.MovementOwner.Controller?.MoveTo
        ?? throw new InvalidOperationException(
            "The character has no walk manager, so there is nothing to order.");

    /// <summary>
    /// How the walk was installed and how it is going: what kind of movement
    /// it is, what it is aimed at, how close it means to get, how fast, and
    /// whether it has stopped getting anywhere.
    /// </summary>
    private static void RecordOrder(ParityTranscript transcript, ParityArm arm)
    {
        MoveToManager moveTo = MoveTo(arm);
        transcript.Record("installed", moveTo.MovementTypeState.ToString());
        transcript.Record("moving", moveTo.IsMovingTo());
        transcript.Record("sought", moveTo.SoughtObjectId);
        transcript.Record("soughtRadius", moveTo.SoughtObjectRadius);
        transcript.Record("distanceToObject", moveTo.Params.DistanceToObject);
        transcript.Record("canCharge", moveTo.Params.CanCharge);
        transcript.Record("stalled", moveTo.FailProgressCount);
        transcript.Record(
            "runRate",
            arm.Runtime.MovementOwner.Controller!.Movement.Minterp.MyRunRate);
    }

    private static void RecordWhere(
        ParityTranscript transcript, INavigationAutomation navigation)
    {
        PluginNavigationSnapshot snapshot = navigation.Snapshot;
        transcript.Record("moving", snapshot.IsMoving);
        transcript.Record("cell", snapshot.Position.CellId);
        transcript.Record("eastWest", snapshot.Position.EastWest);
        transcript.Record("northSouth", snapshot.Position.NorthSouth);
        transcript.Record("heading", snapshot.Position.HeadingDegrees);
        transcript.Record(
            "distance", Distance(navigation, ParityWorld.SecondMonster));
    }


    private static double Distance(
        INavigationAutomation navigation, uint objectId) =>
        navigation.TryGetObject(objectId, out PluginNavigationObject value)
            ? navigation.Snapshot.Position.HorizontalDistanceMeters(
                value.Position)
            : double.NaN;
}
