using AcDream.Core.Combat;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A plugin naming a creature and swinging at it. This is the path that broke:
/// one client swung at what the plugin named and the other at whatever the
/// selection happened to hold, and nothing compared them.
///
/// Every step is recorded AND asserted, on the plugin's answer and on the
/// swing itself: the request the client puts on the wire, with the creature,
/// the height and the power it was asked for. The comparison alone cannot
/// tell a client that swung from one that quietly did nothing, because two
/// clients doing nothing agree line for line -- and until the wait for the
/// power bar was added below, no swing left either client in this file.
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
            ParityWorld.Stage(arm);
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

            transcript.Step("the swing goes out");
            // The bar was let go of before it had reached the power the
            // plugin asked for, so the client keeps loading it and swings
            // when it gets there. Without these frames nothing ever leaves
            // either client and the two agree about the silence.
            AdvanceUntilTheSwingLeaves(arm);
            RecordSnapshot(transcript, combat);
            AssertTheSwing(arm, ParityWorld.Monster, PluginAttackHeight.Medium, 0.5f);
            transcript.RecordOutbound(arm);
        });

    [Fact]
    public void NamingATargetWhileAnotherIsSelectedSwingsAtTheNamedOne() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm);
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
            AdvanceUntilTheSwingLeaves(arm);
            transcript.Record("selected", arm.Host.Selection.SelectedObjectId);
            // The swing names the creature the PLUGIN named, not the one
            // that happened to be selected when it asked. A client that
            // resolved the target from the selection would swing at the
            // other creature, and a scenario that only compared the two
            // clients would still pass if both did.
            AssertTheSwing(arm, ParityWorld.Monster, PluginAttackHeight.High, 1f);
            Assert.Equal(ParityWorld.Monster, arm.Host.Selection.SelectedObjectId);
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// A creature the client has been told is dead. Neither client turns
    /// the swing away -- the plugin is told it started -- and neither
    /// swings at the creature it named: the swing goes out at the live one
    /// standing next to it.
    /// </summary>
    /// <remarks>
    /// That is what both clients do today, written down so it cannot change
    /// on one of them quietly. It also reads like a defect -- a plugin that
    /// names a corpse gets a swing at something else, and is told nothing
    /// about the swap -- and the day it is fixed this scenario is where the
    /// change shows up.
    /// </remarks>
    [Fact]
    public void ADeadTargetIsTakenTheSameWayOnBothClients() =>
        TargetScenario(
            ParityWorld.DeadMonster,
            PluginCombatCommandStatus.Started,
            swingsAt: ParityWorld.Monster);

    /// <summary>
    /// A creature the server has stopped showing. Neither client turns it
    /// away either, and the swing again goes out at the live creature
    /// rather than the named one. See the remark above.
    /// </summary>
    [Fact]
    public void AHiddenTargetIsTakenTheSameWayOnBothClients() =>
        TargetScenario(
            ParityWorld.HiddenMonster,
            PluginCombatCommandStatus.Started,
            swingsAt: ParityWorld.Monster);

    [Fact]
    public void ACreatureThatIsNotHostileIsRefusedTheSameWay() =>
        TargetScenario(
            ParityWorld.Bystander,
            PluginCombatCommandStatus.InvalidTarget,
            swingsAt: null);

    [Fact]
    public void AnUnknownGuidIsRefusedTheSameWay() =>
        TargetScenario(
            0x5000BEEFu,
            PluginCombatCommandStatus.InvalidTarget,
            swingsAt: null);

    /// <summary>
    /// Out of a combat stance, a named target is refused for the stance and
    /// not for the target, on both clients.
    /// </summary>
    [Fact]
    public void OutOfCombatModeTheStanceIsWhatRefuses() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            ICombatAutomation combat = arm.Host.Automation.Combat;

            transcript.Step("begin without a stance");
            PluginCombatCommandResult begun = combat.BeginPhysicalAttack(
                ParityWorld.Monster, PluginAttackHeight.Medium, 0.5f);
            Record(transcript, begun);
            RecordSnapshot(transcript, combat);
            AdvanceUntilTheSwingLeaves(arm);
            // The stance is what refuses, the target is left alone, and no
            // swing leaves the client however long it is given -- which is
            // the half a comparison of two idle clients cannot see.
            Assert.Equal(PluginCombatCommandStatus.WrongMode, begun.Status);
            Assert.Equal(PluginCombatMode.Peace, combat.Snapshot.Mode);
            Assert.Null(arm.Host.Selection.SelectedObjectId);
            Assert.Empty(Swings(arm));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Naming one creature and swinging at it, with what both clients are
    /// expected to answer and whether a swing is expected to leave them.
    /// </summary>
    /// <param name="target">The creature the plugin names.</param>
    /// <param name="expected">What both clients answer the plugin.</param>
    /// <param name="swingsAt">
    /// Which creature a swing really goes out at, or nothing when no swing
    /// leaves the client at all.
    /// </param>
    private static void TargetScenario(
        uint target,
        PluginCombatCommandStatus expected,
        uint? swingsAt) =>
        ParityScenario.Run((arm, transcript) =>
        {
            ParityWorld.Stage(arm);
            ICombatAutomation combat = arm.Host.Automation.Combat;
            _ = combat.EnterMode(PluginCombatMode.Melee);
            arm.Advance();
            _ = arm.Operations.TakeOutbound();

            transcript.Step("begin");
            PluginCombatCommandResult begun = combat.BeginPhysicalAttack(
                target, PluginAttackHeight.Medium, 0.5f);
            Record(transcript, begun);
            transcript.Record("selected", arm.Host.Selection.SelectedObjectId);
            RecordSnapshot(transcript, combat);
            // The answer itself, per arm. Two clients that both said the
            // same wrong word would agree line for line.
            Assert.Equal(expected, begun.Status);
            Assert.Equal(swingsAt is not null, combat.Snapshot.BuildInProgress);

            transcript.Step("release");
            Record(transcript, combat.ReleasePhysicalAttack());
            AdvanceUntilTheSwingLeaves(arm);
            RecordSnapshot(transcript, combat);
            // And what really left the client for it.
            if (swingsAt is { } swung)
                AssertTheSwing(arm, swung, PluginAttackHeight.Medium, 0.5f);
            else
                Assert.Empty(Swings(arm));
            transcript.RecordOutbound(arm);
        });

    /// <summary>
    /// Long enough for a power bar let go of early to finish loading and
    /// fire: the bar fills in a second at the shared step length.
    /// </summary>
    private const int TicksForAFullPowerBar = 80;

    /// <summary>The client action that carries a melee swing.</summary>
    private const uint MeleeAttackAction = 0x0008u;

    private static void AdvanceUntilTheSwingLeaves(ParityArm arm)
    {
        for (int step = 0; step < TicksForAFullPowerBar; step++)
            arm.Advance();
    }

    /// <summary>Every swing this arm has asked to send.</summary>
    private static IReadOnlyList<ParityOutbound> Swings(ParityArm arm)
        => [.. arm.Operations.Outbound.Where(
            message => message.GameAction == MeleeAttackAction)];

    /// <summary>
    /// That one swing left this client, at the creature the plugin named,
    /// at the height and the power it asked for. The three fields follow
    /// the action in the request's body.
    /// </summary>
    private static void AssertTheSwing(
        ParityArm arm, uint target, PluginAttackHeight height, float power)
    {
        ParityOutbound swing = Assert.Single(Swings(arm));
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
            $"the {arm.Name} client swung {(AttackHeight)swungHigh}, not {height}");
        Assert.True(
            Math.Abs(swungPower - power) < 0.001f,
            $"the {arm.Name} client swung at {swungPower:0.000} power, not {power:0.000}");
    }

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
