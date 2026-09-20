using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// What a bot actually does once it has a character standing in the world:
/// build a swing and let it go, then kill what it was swinging at and move
/// straight on to the next creature. None of this ran before the two arms had
/// a body -- the swing was refused on both clients for want of one, and the
/// two agreeing about that proved nothing.
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

            transcript.Step("enter melee");
            Record(transcript, combat.EnterMode(PluginCombatMode.Melee));
            arm.Advance();
            RecordReadiness(transcript, arm);
            transcript.RecordOutbound(arm);

            transcript.Step("begin");
            Record(transcript, combat.BeginPhysicalAttack(
                ParityWorld.Monster, PluginAttackHeight.High, 0.85f));
            RecordSnapshot(transcript, combat);
            RecordReadiness(transcript, arm);

            transcript.Step("hold");
            for (int step = 0; step < 8; step++)
            {
                arm.Advance();
                RecordSnapshot(transcript, combat);
            }

            transcript.Step("release");
            Record(transcript, combat.ReleasePhysicalAttack());
            arm.Advance();
            RecordSnapshot(transcript, combat);
            RecordReadiness(transcript, arm);
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

            transcript.Step("it dies");
            arm.Deliver(static runtime =>
            {
                runtime.ActionOwner.Combat.OnUpdateHealth(
                    ParityWorld.Monster, 0f);
                runtime.CommunicationOwner.Chat.OnSystemMessage(
                    "You have slain the creature!", chatType: 0x00u);
            });
            arm.Advance();
            transcript.Record(
                "selected", arm.Host.Selection.SelectedObjectId);
            transcript.Record(
                "dead", arm.Runtime.ActionOwner.CreatureDeath.IsDead(
                    ParityWorld.Monster));
            transcript.Record(
                "chat.lines", arm.Runtime.CommunicationOwner.Chat.Count);
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
            Record(transcript, combat.BeginPhysicalAttack(
                ParityWorld.Monster, PluginAttackHeight.Medium, 0.5f));
            transcript.RecordOutbound(arm);
            RecordReadiness(transcript, arm);

            transcript.Step("release");
            Record(transcript, combat.ReleasePhysicalAttack());
            arm.Advance();
            transcript.RecordOutbound(arm);
        });

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
