using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A plugin naming a creature and swinging at it. This is the path that broke:
/// one client swung at what the plugin named and the other at whatever the
/// selection happened to hold, and nothing compared them.
///
/// Mutation check (2026-09-20): making the windowed arm resolve its target
/// from the selection at swing time -- reading
/// <c>Runtime.ActionOwner.Selection.SelectedObjectId</c> in place of the
/// target source's answer inside <c>LiveCombatAttackOperations.SendAttack</c>
/// -- turned
/// <see cref="NamingATargetWhileAnotherIsSelectedSwingsAtTheNamedOne"/> red
/// with a different outbound attack packet on each client. Restoring it
/// turned the test green.
/// </summary>
public sealed class AttackParityTests
{
    [Fact]
    public void NamingATargetWithNothingSelectedSwingsAtIt() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm.Runtime);
            ICombatAutomation combat = arm.Host.Automation.Combat;

            transcript.Step("enter melee");
            Record(transcript, combat.EnterMode(PluginCombatMode.Melee));
            arm.Advance();
            transcript.RecordOutbound(arm);

            transcript.Step("begin");
            Record(transcript, combat.BeginPhysicalAttack(
                ParityWorld.Monster, PluginAttackHeight.Medium, 0.5f));
            RecordSnapshot(transcript, combat);

            transcript.Step("hold");
            for (int step = 0; step < 4; step++)
            {
                arm.Advance();
                RecordSnapshot(transcript, combat);
            }

            transcript.Step("release");
            Record(transcript, combat.ReleasePhysicalAttack());
            arm.Advance();
            RecordSnapshot(transcript, combat);
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void NamingATargetWhileAnotherIsSelectedSwingsAtTheNamedOne() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm.Runtime);
            ICombatAutomation combat = arm.Host.Automation.Combat;
            _ = combat.EnterMode(PluginCombatMode.Melee);
            arm.Host.Selection.Select(ParityWorld.SecondMonster);
            arm.Advance();
            _ = arm.Operations.TakeOutbound();

            transcript.Step("begin on the other creature");
            Record(transcript, combat.BeginPhysicalAttack(
                ParityWorld.Monster, PluginAttackHeight.High, 1f));
            transcript.Record("selected", arm.Host.Selection.SelectedObjectId);

            transcript.Step("release");
            Record(transcript, combat.ReleasePhysicalAttack());
            arm.Advance();
            transcript.Record("selected", arm.Host.Selection.SelectedObjectId);
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void ADeadTargetIsRefusedTheSameWay() =>
        RefusalScenario(ParityWorld.DeadMonster);

    [Fact]
    public void AHiddenTargetIsRefusedTheSameWay() =>
        RefusalScenario(ParityWorld.HiddenMonster);

    [Fact]
    public void ACreatureThatIsNotHostileIsRefusedTheSameWay() =>
        RefusalScenario(ParityWorld.Bystander);

    [Fact]
    public void AnUnknownGuidIsRefusedTheSameWay() =>
        RefusalScenario(0x5000BEEFu);

    /// <summary>
    /// Out of a combat stance, a named target is refused for the stance and
    /// not for the target, on both clients.
    /// </summary>
    [Fact]
    public void OutOfCombatModeTheStanceIsWhatRefuses() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm.Runtime);
            ICombatAutomation combat = arm.Host.Automation.Combat;

            transcript.Step("begin without a stance");
            Record(transcript, combat.BeginPhysicalAttack(
                ParityWorld.Monster, PluginAttackHeight.Medium, 0.5f));
            RecordSnapshot(transcript, combat);
            arm.Advance();
            transcript.RecordOutbound(arm);
        });

    private static void RefusalScenario(uint target) =>
        ParityScenario.Run((arm, transcript) =>
        {
            ParityWorld.Stage(arm.Runtime);
            ICombatAutomation combat = arm.Host.Automation.Combat;
            _ = combat.EnterMode(PluginCombatMode.Melee);
            arm.Advance();
            _ = arm.Operations.TakeOutbound();

            transcript.Step("begin");
            Record(transcript, combat.BeginPhysicalAttack(
                target, PluginAttackHeight.Medium, 0.5f));
            transcript.Record("selected", arm.Host.Selection.SelectedObjectId);
            RecordSnapshot(transcript, combat);

            transcript.Step("release");
            Record(transcript, combat.ReleasePhysicalAttack());
            arm.Advance();
            RecordSnapshot(transcript, combat);
            transcript.RecordOutbound(arm);
        });

    private static void Record(
        ParityTranscript transcript, PluginCombatCommandResult result)
    {
        transcript.Record("status", result.Status);
        transcript.Record("notice", result.Notice);
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
        transcript.Record(
            "snapshot.completionRevision", snapshot.CompletionRevision);
        transcript.Record(
            "snapshot.completionSequence", snapshot.CompletionSequence);
    }
}
