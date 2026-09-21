using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// How fast the character runs, as the server decides it. Run skill and
/// stamina both come down the wire, and the client turns them into the rate
/// the character actually covers ground at: a better runner goes further per
/// second, and a character out of stamina runs as though it had no skill at
/// all.
///
/// This is the first scenario over the hooks that carry it. They were left
/// out of the harness's hand-written character bindings, so a client could
/// have stopped applying either and nothing here would have noticed -- a bot
/// walking at the wrong speed misses every timed step it takes.
///
/// Distance is measured rather than rates read out of the client's insides:
/// what a plugin sees is where the character got to.
///
/// Mutation check (2026-09-20), run: taking both movement hooks out of the
/// windowless client's character bindings turned both scenarios red -- the
/// character covered the same ground before and after the skill went up, and
/// kept its pace with no stamina left. Taking out either hook alone left
/// them green, because the two both end at the same owner: one hook is
/// enough, which is worth knowing. Restoring them turned them green.
/// </summary>
public sealed class MovementStatsParityTests
{
    /// <summary>The run skill.</summary>
    private const uint RunSkillId = 24u;

    /// <summary>
    /// The jump skill. The client applies nothing until it knows both, so a
    /// scenario about running has to state this one as well.
    /// </summary>
    private const uint JumpSkillId = 22u;

    /// <summary>The character's stamina, as the wire numbers the vitals.</summary>
    private const uint StaminaVitalId = 4u;

    /// <summary>Half a second at the shared step length.</summary>
    private const int TicksForHalfASecond = 33;

    [Fact]
    public void TheServersRunSkillChangesHowFarTheCharacterGetsOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            INavigationAutomation navigation = Stage(arm, runSkill: 10u);

            transcript.Step("a poor runner covers this much ground");
            double plodded = WalkForwardHalfASecond(arm, navigation);
            transcript.Record("plodded", Round(plodded));

            transcript.Step("the server says the character got better at it");
            arm.Server.SkillUpdate(RunSkillId, ranks: 300u);
            arm.Advance();

            transcript.Step("and now covers more");
            double ran = WalkForwardHalfASecond(arm, navigation);
            transcript.Record("ran", Round(ran));
            // Said outright: two clients that both ignored the new skill
            // would agree with each other and be wrong together.
            Assert.True(
                ran > plodded,
                $"The better runner covered {ran:F3} metres against "
                + $"{plodded:F3} before the skill went up, so the client is "
                + "not applying what the server said.");
        });

    [Fact]
    public void RunningOutOfStaminaSlowsTheCharacterOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            INavigationAutomation navigation = Stage(arm, runSkill: 300u);

            transcript.Step("with stamina in hand");
            double ran = WalkForwardHalfASecond(arm, navigation);
            transcript.Record("ran", Round(ran));

            transcript.Step("the server drains the last of it");
            arm.Server.VitalUpdate(StaminaVitalId, current: 0u);
            arm.Advance();

            transcript.Step("and the character labours");
            double laboured = WalkForwardHalfASecond(arm, navigation);
            transcript.Record("laboured", Round(laboured));
            Assert.True(
                laboured < ran,
                $"Out of stamina the character still covered {laboured:F3} "
                + $"metres against {ran:F3} with stamina in hand, so the "
                + "client is not applying the drained vital.");
        });

    /// <summary>
    /// A character with a body, and a run skill and a full stamina bar the
    /// server has stated, so the client has a complete picture to apply.
    /// </summary>
    private static INavigationAutomation Stage(ParityArm arm, uint runSkill)
    {
        _ = ParityWorld.Stage(arm);
        arm.Server.SkillUpdate(JumpSkillId, ranks: 50u);
        arm.Server.SkillUpdate(RunSkillId, ranks: runSkill);
        arm.Server.VitalUpdate(StaminaVitalId, current: 100u);
        arm.Advance();
        _ = arm.Operations.TakeOutbound();
        return arm.Host.Automation.Navigation;
    }

    /// <summary>
    /// Holds forward for half a second and answers how far north the
    /// character got, then lets go and settles.
    /// </summary>
    private static double WalkForwardHalfASecond(
        ParityArm arm,
        INavigationAutomation navigation)
    {
        double from = navigation.Snapshot.Position.NorthSouth;
        _ = navigation.SetMovementIntent(
            new PluginMovementIntent(Forward: true, Run: true));
        for (int step = 0; step < TicksForHalfASecond; step++)
            arm.Advance();
        double travelled = navigation.Snapshot.Position.NorthSouth - from;
        _ = navigation.ClearMovementIntent();
        _ = navigation.StopMoving();
        for (int step = 0; step < TicksForHalfASecond; step++)
            arm.Advance();
        return Math.Abs(travelled);
    }

    /// <summary>
    /// To the millimetre. Both clients walk the same physics, so they agree
    /// exactly; rounding keeps the transcript readable.
    /// </summary>
    private static double Round(double metres) => Math.Round(metres, 3);
}
