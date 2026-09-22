using AcDream.Core.Combat;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// What a bot actually does once it has a character standing in the world:
/// build a swing and let it go, then kill what it was swinging at and move
/// straight on to the next creature. None of this ran before the two arms had
/// a body -- the swing was refused on both clients for want of one, and the
/// two agreeing about that proved nothing.
///
/// Each step is recorded AND asserted: the plugin's answer, the power bar
/// as it climbs, and the swing that finally leaves the client with the
/// creature, the height and the power the plugin asked for. Recording alone
/// compares the two clients and nothing else -- and with no wait for the bar
/// to finish loading, no swing left either of them, which the comparison was
/// perfectly happy with.
///
/// Mutation checks (2026-09-21), each run: making the power bar take two
/// seconds to fill instead of one turned
/// <see cref="HoldingAndReleasingASwingRunsTheSameOnBothClients"/> red at the
/// first step of the bar; sending every swing aimed at the middle turned the
/// same scenario red on the height. Both clients were wrong together in each
/// case, so nothing but the assertions could see it.
///
/// Mutation checks (2026-09-20), each run:
/// * Before the clock was made one clock, the windowed client timed its
///   power-up off a wall clock:
///   <see cref="HoldingAndReleasingASwingRunsTheSameOnBothClients"/> was red
///   with a different power-bar level at every step (windowed 0.0004 to
///   0.0027 against windowless 0.0000 to 0.0750).
/// * Before the stop for a swing was made one implementation, only the
///   windowed client pushed the stop out:
///   <see cref="AskingForASwingWhileRunningStopsTheSameWayOnBothClients"/>
///   was red with one outbound packet on the windowed client and none on the
///   other.
/// * Making the windowed client answer "not ready to swing" no matter what
///   turned this file's two swing scenarios red, along with three of the
///   older attack ones. Restoring it turned them green.
/// </summary>
public sealed class BodiedCombatParityTests
{
    /// <summary>
    /// The whole power-bar sequence at a named creature, with the power and
    /// height the plugin asked for and no selection anywhere near it.
    /// </summary>
    [Fact]
    public void HoldingAndReleasingASwingRunsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ICombatAutomation combat = arm.Host.Automation.Combat;

            transcript.Step("standing");
            transcript.Record("body", arm.HasLiveBody);
            RecordReadiness(transcript, arm);
            // A character with no body cannot swing at all, and two clients
            // with no body agree about everything below.
            Assert.True(arm.HasLiveBody, $"{arm.Name} has no character.");
            Assert.False(
                arm.Dependencies.CombatAttackOperations.PlayerReadyForAttack,
                $"{arm.Name} says it can swing before it is in a stance.");

            transcript.Step("enter melee");
            PluginCombatCommandResult stance =
                combat.EnterMode(PluginCombatMode.Melee);
            Record(transcript, stance);
            arm.Advance();
            RecordReadiness(transcript, arm);
            Assert.Equal(PluginCombatCommandStatus.ModeChangeSent, stance.Status);
            Assert.True(
                arm.Dependencies.CombatAttackOperations.PlayerReadyForAttack,
                $"{arm.Name} cannot swing after entering a stance.");
            transcript.RecordOutbound(arm);

            transcript.Step("begin");
            PluginCombatCommandResult begun = combat.BeginPhysicalAttack(
                ParityWorld.Monster, PluginAttackHeight.High, HeldPower);
            Record(transcript, begun);
            RecordSnapshot(transcript, combat);
            RecordReadiness(transcript, arm);
            // The client took the creature, the height and the power the
            // plugin named, and has started loading the bar.
            Assert.Equal(PluginCombatCommandStatus.Started, begun.Status);
            Assert.Equal(ParityWorld.Monster, combat.Snapshot.SelectedObjectId);
            Assert.Equal(PluginCombatMode.Melee, combat.Snapshot.Mode);
            Assert.Equal(PluginAttackHeight.High, combat.Snapshot.AttackHeight);
            Assert.Equal(HeldPower, combat.Snapshot.DesiredPower, 3);
            Assert.True(combat.Snapshot.BuildInProgress);

            transcript.Step("hold");
            for (int step = 0; step < HoldSteps; step++)
            {
                arm.Advance();
                RecordSnapshot(transcript, combat);
                // The bar fills in a second, so one step of the shared clock
                // is worth one step of the bar. Two clients timing it off
                // two different clocks disagree here; two clients whose bar
                // never moved at all would agree.
                Assert.Equal(
                    ParityArm.TickSeconds * (step + 1),
                    combat.Snapshot.PowerBarLevel,
                    3);
            }

            transcript.Step("release");
            Record(transcript, combat.ReleasePhysicalAttack());
            arm.Advance();
            RecordSnapshot(transcript, combat);
            RecordReadiness(transcript, arm);
            transcript.RecordOutbound(arm);

            transcript.Step("the swing goes out");
            // Let go of early, so the client keeps loading the bar to the
            // power that was asked for and swings when it gets there.
            AdvanceUntilTheSwingLeaves(arm);
            RecordSnapshot(transcript, combat);
            AssertTheSwing(
                arm, ParityWorld.Monster, PluginAttackHeight.High, HeldPower);
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// The creature dies mid-swing: what the client then believes about it,
    /// what it says, and the swing that goes straight to the next one.
    /// </summary>
    [Fact]
    public void AKillBurstMovesOnToTheNextCreatureTheSameWay() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ICombatAutomation combat = arm.Host.Automation.Combat;
            _ = combat.EnterMode(PluginCombatMode.Melee);
            arm.Advance();
            _ = arm.Operations.TakeOutbound();

            transcript.Step("begin on the first creature");
            Record(transcript, combat.BeginPhysicalAttack(
                ParityWorld.Monster, PluginAttackHeight.Medium, 0.5f));
            RecordTargets(transcript, combat);

            transcript.Step("release");
            Record(transcript, combat.ReleasePhysicalAttack());
            arm.Advance();
            transcript.RecordOutbound(arm);

            // The kill arrives the way the server sends it -- a health
            // update and a line of text -- through each client's own inbound
            // route, rather than being poked into the runtime by hand.
            transcript.Step("it dies");
            arm.Server.UpdateHealth(ParityWorld.Monster, 0f);
            arm.Server.SystemMessage("You have slain the creature!", chatType: 0x00u);
            arm.Advance();
            transcript.Record(
                "selected", arm.Host.Selection.SelectedObjectId);
            transcript.Record(
                "dead", arm.Runtime.ActionOwner.CreatureDeath.IsDead(
                    ParityWorld.Monster));
            transcript.Record(
                "chat.lines", arm.Runtime.CommunicationOwner.Chat.Count);
            // Said outright: two clients that both heard nothing would
            // agree, and agreeing is not the point.
            Assert.True(arm.Runtime.ActionOwner.Combat.HasHealth(
                ParityWorld.Monster));
            Assert.Equal(
                0f,
                arm.Runtime.ActionOwner.Combat.GetHealthPercent(
                    ParityWorld.Monster));
            Assert.True(arm.Runtime.CommunicationOwner.Chat.Count > 0);
            RecordTargets(transcript, combat);

            transcript.Step("begin on the next creature");
            Record(transcript, combat.BeginPhysicalAttack(
                ParityWorld.SecondMonster, PluginAttackHeight.Low, 1f));
            Record(transcript, combat.ReleasePhysicalAttack());
            arm.Advance();
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// The swing is asked for while the character is running. A client stops
    /// the character before it asks the server for a swing, and what it tells
    /// the server about that stop is a packet a plugin's swing produces --
    /// so both clients have to produce it, and at the same step.
    /// </summary>
    [Fact]
    public void AskingForASwingWhileRunningStopsTheSameWayOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            ICombatAutomation combat = arm.Host.Automation.Combat;
            _ = combat.EnterMode(PluginCombatMode.Melee);
            arm.Deliver(static runtime => runtime.MovementOwner.SetCommandInput(
                new AcDream.Runtime.Gameplay.MovementInput(
                    Forward: true, Run: true)));
            for (int step = 0; step < 13; step++)
                arm.Advance();
            _ = arm.Operations.TakeOutbound();

            transcript.Step("begin while running");
            PluginCombatCommandResult begun = combat.BeginPhysicalAttack(
                ParityWorld.Monster, PluginAttackHeight.Medium, 0.5f);
            Record(transcript, begun);
            // The stop is the point of this one: before it asks the server
            // for a swing the client stops the character, and it TELLS the
            // server so. A client that only stopped locally sends nothing
            // here, and two clients that both sent nothing agree.
            Assert.Equal(PluginCombatCommandStatus.Started, begun.Status);
            Assert.NotEmpty(Sent(arm, MoveToStateAction));
            transcript.Record(
                "moving", arm.Host.Automation.Navigation.Snapshot.IsMoving);
            transcript.Record("forwardCommand", ForwardCommand(arm));
            // And it really stopped the character rather than only saying
            // so: its own body is standing ready instead of running. The
            // plugin is still holding the run down, which is why the
            // navigation snapshot still calls it moving.
            Assert.Equal(CombatInputPlanner.ReadyForwardCommand, ForwardCommand(arm));
            transcript.RecordOutbound(arm);
            RecordReadiness(transcript, arm);

            transcript.Step("release");
            Record(transcript, combat.ReleasePhysicalAttack());
            AdvanceUntilTheSwingLeaves(arm);
            transcript.Record(
                "moving", arm.Host.Automation.Navigation.Snapshot.IsMoving);
            // And the swing itself still goes out, at the named creature.
            AssertTheSwing(
                arm, ParityWorld.Monster, PluginAttackHeight.Medium, 0.5f);
            transcript.RecordOutbound(arm);
        });

    /// <summary>The power the plugin asks the bar to be loaded to.</summary>
    private const float HeldPower = 0.85f;

    /// <summary>How many steps the bar is watched climbing for.</summary>
    private const int HoldSteps = 8;

    /// <summary>
    /// Long enough for a bar let go of early to finish loading and fire:
    /// the bar fills in a second at the shared step length.
    /// </summary>
    private const int TicksForAFullPowerBar = 80;

    /// <summary>The client action that carries a melee swing.</summary>
    private const uint MeleeAttackAction = 0x0008u;

    /// <summary>The client action that says where the character is going.</summary>
    private const uint MoveToStateAction = 0xF61Cu;

    /// <summary>What the character is telling its own body to do.</summary>
    private static uint ForwardCommand(ParityArm arm) =>
        arm.Runtime.MovementOwner.Controller?.Movement.Minterp
            .InterpretedState.ForwardCommand ?? 0u;

    private static void AdvanceUntilTheSwingLeaves(ParityArm arm)
    {
        for (int step = 0; step < TicksForAFullPowerBar; step++)
            arm.Advance();
    }

    /// <summary>
    /// Everything this arm asked to send that carries one named client
    /// action. Counting messages will not do it: a character standing in
    /// the world is telling the server where it is the whole time.
    /// </summary>
    private static IReadOnlyList<ParityOutbound> Sent(ParityArm arm, uint action)
        => [.. arm.Operations.Outbound.Where(
            message => message.GameAction == action)];

    /// <summary>
    /// That one swing left this client, at the creature the plugin named,
    /// at the height and the power it asked for.
    /// </summary>
    private static void AssertTheSwing(
        ParityArm arm, uint target, PluginAttackHeight height, float power)
    {
        ParityOutbound swing = Assert.Single(Sent(arm, MeleeAttackAction));
        byte[] body = Convert.FromHexString(swing.Body);
        uint swungAt = System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(body.AsSpan(12));
        uint swungHigh = System.Buffers.Binary.BinaryPrimitives
            .ReadUInt32LittleEndian(body.AsSpan(16));
        float swungPower = System.Buffers.Binary.BinaryPrimitives
            .ReadSingleLittleEndian(body.AsSpan(20));
        Assert.True(
            swungAt == target,
            $"the {arm.Name} client swung at 0x{swungAt:X8}, not 0x{target:X8}");
        Assert.True(
            swungHigh == (uint)height,
            $"the {arm.Name} client swung at height {swungHigh}, not {height}");
        Assert.True(
            Math.Abs(swungPower - power) < 0.001f,
            $"the {arm.Name} client swung at {swungPower:0.000} power, "
            + $"not {power:0.000}");
    }

    private static void Record(
        ParityTranscript transcript, PluginCombatCommandResult result)
    {
        transcript.Record("status", result.Status);
        transcript.Record("notice", result.Notice);
    }

    /// <summary>
    /// Whether the character is standing in a position it can swing from, and
    /// whether it is holding two weapons. Both are read off this host's own
    /// combat operations, which is the seam the two clients answered from
    /// different places.
    /// </summary>
    private static void RecordReadiness(ParityTranscript transcript, ParityArm arm)
    {
        transcript.Record(
            "readyForAttack",
            arm.Dependencies.CombatAttackOperations.PlayerReadyForAttack);
        transcript.Record(
            "dualWield",
            arm.Dependencies.CombatAttackOperations.IsDualWield);
    }

    private static void RecordTargets(
        ParityTranscript transcript, ICombatAutomation combat)
    {
        IReadOnlyList<PluginCombatTarget> targets =
            combat.CaptureHostileTargets(25f);
        transcript.Record("targets.count", targets.Count);
        for (int index = 0; index < targets.Count; index++)
        {
            PluginCombatTarget target = targets[index];
            transcript.Record($"target[{index}].id", target.ObjectId);
            transcript.Record($"target[{index}].dead", target.IsDead);
            transcript.Record($"target[{index}].distance", target.Distance);
        }
    }

    private static void RecordSnapshot(
        ParityTranscript transcript, ICombatAutomation combat)
    {
        PluginCombatSnapshot snapshot = combat.Snapshot;
        transcript.Record("snapshot.selected", snapshot.SelectedObjectId);
        transcript.Record("snapshot.mode", snapshot.Mode);
        transcript.Record("snapshot.height", snapshot.AttackHeight);
        transcript.Record("snapshot.desiredPower", snapshot.DesiredPower);
        transcript.Record("snapshot.powerBar", snapshot.PowerBarLevel);
        transcript.Record("snapshot.building", snapshot.BuildInProgress);
        transcript.Record("snapshot.requesting", snapshot.RequestInProgress);
        transcript.Record("snapshot.awaitingServer", snapshot.ServerResponsePending);
        transcript.Record("snapshot.repeating", snapshot.RepeatAttackInProgress);
    }
}
