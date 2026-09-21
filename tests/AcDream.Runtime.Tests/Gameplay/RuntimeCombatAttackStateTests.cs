using AcDream.Core.Combat;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeCombatAttackStateTests
{
    [Fact]
    public void NormalStance_ChargesLinearlyOverOneSecond_AndReleaseSendsCurrentPower()
    {
        double now = 10d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Melee);

        controller.PressAttack(AttackHeight.High);
        now = 10.5d;

        Assert.Equal(0.5f, controller.PowerBarLevel, 3);
        controller.ReleaseAttack();

        var attack = Assert.Single(sent);
        Assert.Equal(AttackHeight.High, attack.Height);
        Assert.Equal(0.5f, attack.Power, 3);
    }

    [Fact]
    public void DualWieldStance_UsesRetailPointEightSecondPowerUpTime()
    {
        double now = 3d;
        var combat = new CombatState();
        using var controller = Create(
            combat,
            () => now,
            [],
            isDualWield: () => true);
        combat.SetCombatMode(CombatMode.Melee);

        controller.PressAttack(AttackHeight.Medium);
        now = 3.4d;

        Assert.Equal(0.5f, controller.PowerBarLevel, 3);
    }

    [Fact]
    public void EarlyRelease_KeepsLoadingToTheSetPowerBeforeCommitting()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Missile);
        controller.SetDesiredPower(1f);

        controller.PressAttack(AttackHeight.Low);
        now = 0.25d;
        controller.ReleaseAttack();
        Assert.Empty(sent);

        now = 0.26d;
        controller.Tick();
        Assert.Empty(sent);

        now = 0.99d;
        controller.Tick();
        Assert.Empty(sent);

        now = 1d;
        controller.Tick();

        var attack = Assert.Single(sent);
        Assert.Equal(AttackHeight.Low, attack.Height);
        Assert.Equal(1f, attack.Power, 3);
    }

    [Fact]
    public void ReleasePastTheSetPower_FiresImmediatelyAtTheHeldLevel()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Melee);
        controller.SetDesiredPower(0.5f);

        controller.PressAttack(AttackHeight.Medium);
        now = 0.8d;
        controller.ReleaseAttack();

        Assert.Equal(2, sent.Count);
        Assert.Equal(AttackHeight.Medium, sent[0].Height);
        Assert.Equal(0.8f, sent[0].Power, 3);
        Assert.Equal(AttackHeight.Medium, sent[1].Height);
        Assert.Equal(0.5f, sent[1].Power, 3);
    }

    [Fact]
    public void ChargedRelease_ImmediatelyFollowsWithTheBarSetting()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(
            combat,
            () => now,
            sent,
            autoRepeatAttack: () => true);
        combat.SetCombatMode(CombatMode.Melee);
        controller.SetDesiredPower(0f);

        controller.PressAttack(AttackHeight.High);
        now = 1.2d;
        controller.ReleaseAttack();

        Assert.Equal(2, sent.Count);
        Assert.Equal(1f, sent[0].Power, 3);
        Assert.Equal(0f, sent[1].Power, 3);
        Assert.Equal(0f, controller.RequestedAttackPower, 3);
    }

    [Fact]
    public void RepeatAfterAChargedRelease_CarriesTheBarSettingNotTheChargedLevel()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(
            combat,
            () => now,
            sent,
            autoRepeatAttack: () => true);
        combat.SetCombatMode(CombatMode.Melee);
        controller.SetDesiredPower(1f / 6f);

        controller.PressAttack(AttackHeight.Medium);
        now = 1.2d;
        controller.ReleaseAttack();

        // The charged swing, then the level the next swing must use.
        Assert.Equal(2, sent.Count);
        Assert.Equal(1f, sent[0].Power, 3);
        Assert.Equal(1f / 6f, sent[1].Power, 3);

        combat.OnCombatCommenceAttack();
        now = 1.4d;
        combat.OnAttackDone(1u, 0u);

        // Nothing more is requested: the level in flight already matches the bar.
        Assert.Equal(2, sent.Count);
        Assert.True(controller.RepeatAttackInProgress);
        Assert.Equal(1f / 6f, controller.RequestedAttackPower, 3);
    }

    [Fact]
    public void MovingTheBarDuringRepeats_RequestsTheNewLevelAtTheNextCompletion()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(
            combat,
            () => now,
            sent,
            autoRepeatAttack: () => true);
        combat.SetCombatMode(CombatMode.Melee);
        controller.SetDesiredPower(0.25f);

        controller.PressAttack(AttackHeight.Low);
        now = 0.25d;
        controller.ReleaseAttack();
        Assert.Single(sent);

        combat.OnCombatCommenceAttack();
        controller.SetDesiredPower(1f);
        now = 0.5d;
        combat.OnAttackDone(1u, 0u);

        Assert.Equal(2, sent.Count);
        Assert.Equal(1f, sent[1].Power, 3);
        Assert.Equal(AttackHeight.Low, sent[1].Height);
    }

    [Fact]
    public void MissileTapWithoutAutoRepeat_LoadsToTheSetAccuracyBeforeFiring()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Missile);
        controller.SetDesiredPower(2f / 3f);

        controller.PressAttack(AttackHeight.Medium);
        now = 0.05d;
        controller.ReleaseAttack();
        Assert.Empty(sent);

        now = 0.5d;
        controller.Tick();
        Assert.Empty(sent);

        now = 0.7d;
        controller.Tick();

        var attack = Assert.Single(sent);
        Assert.Equal(2f / 3f, attack.Power, 3);
        Assert.False(controller.RepeatAttackInProgress);
    }

    [Fact]
    public void HoldBinding_UsesPressAndReleaseTransitions()
    {
        double now = 1d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Melee);

        Assert.True(controller.HandleCommand(new RuntimeCombatAttackInput(
            RuntimeCombatAttackCommand.LowAttack,
            RuntimeInputActivation.Press)));
        now = 1.5d;
        Assert.True(controller.HandleCommand(new RuntimeCombatAttackInput(
            RuntimeCombatAttackCommand.LowAttack,
            RuntimeInputActivation.Release)));

        Assert.Equal(AttackHeight.Low, Assert.Single(sent).Height);
    }

    [Fact]
    public void LeavingTargetedCombat_CancelsAnActiveBuild()
    {
        double now = 0d;
        var combat = new CombatState();
        using var controller = Create(combat, () => now, []);
        combat.SetCombatMode(CombatMode.Melee);
        controller.PressAttack(AttackHeight.Medium);
        now = 0.4d;

        combat.SetCombatMode(CombatMode.NonCombat);

        Assert.False(controller.BuildInProgress);
        Assert.False(controller.AttackRequestInProgress);
        Assert.Equal(0f, controller.PowerBarLevel);
    }

    [Fact]
    public void RequestWaitsUntilPlayerReachesReadyStanceBeforeBuilding()
    {
        double now = 0d;
        bool ready = false;
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            () => true,
            (_, _) => true,
            playerReadyForAttack: () => ready,
            now: () => now);
        combat.SetCombatMode(CombatMode.Missile);

        controller.PressAttack(AttackHeight.High);
        Assert.True(controller.AttackRequestInProgress);
        Assert.False(controller.BuildInProgress);

        ready = true;
        now = 5d;
        controller.Tick();

        Assert.True(controller.BuildInProgress);
        Assert.Equal(0f, controller.PowerBarLevel);
    }

    [Fact]
    public void MovementAbort_DuringRepeat_SendsCancelAndPreventsNextAttack()
    {
        double now = 20d;
        int cancels = 0;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            canStartAttack: () => true,
            sendAttack: (height, power) =>
            {
                sent.Add((height, power));
                return true;
            },
            sendCancelAttack: () => cancels++,
            autoRepeatAttack: () => true,
            now: () => now);
        combat.SetCombatMode(CombatMode.Melee);

        controller.PressAttack(AttackHeight.Medium);
        now += 0.5d;
        controller.ReleaseAttack();
        Assert.Single(sent);
        Assert.True(controller.RepeatAttackInProgress);

        controller.HandleCommand(new RuntimeCombatAttackInput(
            RuntimeCombatAttackCommand.AbortForMovement,
            RuntimeInputActivation.Press));
        combat.OnAttackDone(1u, 0u);

        Assert.Equal(1, cancels);
        Assert.Single(sent);
        Assert.False(controller.RepeatAttackInProgress);
        Assert.False(controller.BuildInProgress);
        Assert.Equal(0f, controller.PowerBarLevel);
    }

    [Fact]
    public void MovementAbort_WhileIdle_DoesNotSendCancel()
    {
        int cancels = 0;
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            canStartAttack: () => true,
            sendAttack: (_, _) => true,
            sendCancelAttack: () => cancels++);

        controller.AbortAutomaticAttack();

        Assert.Equal(0, cancels);
    }

    [Fact]
    public void AttackDonePublishesOneCompletionReceiptAndResetClearsIt()
    {
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            canStartAttack: () => true,
            sendAttack: (_, _) => true);

        combat.OnAttackDone(47u, 0x1234u);

        Assert.Equal(1, controller.CompletionRevision);
        Assert.Equal(47u, controller.CompletionSequence);
        Assert.Equal(0x1234u, controller.CompletionWeenieError);

        controller.ResetSession();
        Assert.Equal(0, controller.CompletionRevision);
        Assert.Equal(0u, controller.CompletionSequence);
        Assert.Equal(0u, controller.CompletionWeenieError);
    }

    [Fact]
    public void StartAttackRequest_PreparesPlayerMovementBeforePowerBuild()
    {
        var events = new List<string>();
        var combat = new CombatState();
        RuntimeCombatAttackState? controller = null;
        controller = new RuntimeCombatAttackState(
            combat,
            canStartAttack: () => true,
            sendAttack: (_, _) =>
            {
                events.Add("send");
                return true;
            },
            prepareAttackRequest: () =>
            {
                Assert.True(controller!.AttackRequestInProgress);
                Assert.Equal(1f, controller.RequestedAttackPower);
                events.Add("prepare");
            },
            // Frozen clock: the release commits the bar setting exactly, so the
            // charged-release follow-up request does not apply here.
            now: () => 5d);
        using (controller)
        {
        combat.SetCombatMode(CombatMode.Missile);
        controller.SetDesiredPower(0f);

        controller.PressAttack(AttackHeight.Medium);

        Assert.Equal(new[] { "prepare" }, events);
        Assert.True(controller.AttackRequestInProgress);
        Assert.True(controller.BuildInProgress);

        controller.ReleaseAttack();
        Assert.Equal(new[] { "prepare", "send" }, events);
        }
    }

    [Fact]
    public void ResetSession_RestoresRetailBeginDefaultsWithoutSendingCancel()
    {
        double now = 1d;
        int cancels = 0;
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            canStartAttack: () => true,
            sendAttack: (_, _) => true,
            sendCancelAttack: () => cancels++,
            now: () => now);
        combat.SetCombatMode(CombatMode.Melee);
        controller.SetDesiredPower(1f);
        controller.PressAttack(AttackHeight.High);
        now = 1.5d;

        controller.ResetSession();

        Assert.False(controller.AttackRequestInProgress);
        Assert.False(controller.BuildInProgress);
        Assert.Equal(0f, controller.RequestedAttackPower);
        Assert.Equal(0f, controller.PowerBarLevel);
        Assert.Equal(AttackHeight.Medium, controller.RequestedHeight);
        Assert.Equal(RuntimeCombatAttackState.InitialDesiredPower, controller.DesiredPower);
        Assert.Equal(0, cancels);
    }

    [Fact]
    public void AutomationControlledAttacksAskForNoAutoTargetOnStartOrSend()
    {
        double now = 1d;
        var allowed = new List<bool>();
        var combat = new CombatState();
        using var controller = new RuntimeCombatAttackState(
            combat,
            new DelegateRuntimeCombatAttackOperations(
                allow => { allowed.Add(allow); return true; },
                (_, _, allow) => { allowed.Add(allow); return true; },
                prepareAttackRequest: null,
                sendCancelAttack: null,
                isDualWield: null,
                playerReadyForAttack: null,
                autoRepeatAttack: () => true),
            () => now);
        combat.SetCombatMode(CombatMode.Melee);

        controller.AutomationControlled = true;
        controller.PressAttack(AttackHeight.Medium);
        now = 2d;
        controller.ReleaseAttack();
        Assert.NotEmpty(allowed);
        Assert.All(allowed, Assert.False);

        allowed.Clear();
        controller.AutomationControlled = false;
        controller.PressAttack(AttackHeight.Medium);
        now = 3d;
        controller.ReleaseAttack();
        Assert.NotEmpty(allowed);
        Assert.All(allowed, Assert.True);
    }

    /// <summary>
    /// Both hosts bind this one state through the same operations contract, so
    /// what reaches the send here is what reaches the wire under a window and
    /// without one alike. Mutation: commit Math.Max(DesiredPower, currentPower)
    /// for an owner that names its power and the swing goes out at the level
    /// the bar had run on to, followed by a second swing to bring the setting
    /// back.
    /// </summary>
    [Fact]
    public void AnOwnerThatNamesItsPowerGetsExactlyThatPowerInOneSwing()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Melee);
        controller.AutomationControlled = true;
        controller.SetDesiredPower(0.9f);

        controller.PressAttack(AttackHeight.High);
        // The bar is only looked at every so often, and by the time it is
        // looked at it has run past the setting.
        now = 0.94d;
        controller.Tick();
        Assert.True(controller.ReleaseAttack());

        var attack = Assert.Single(sent);
        Assert.Equal(AttackHeight.High, attack.Height);
        Assert.Equal(0.9f, attack.Power, 3);
    }

    /// <summary>
    /// A player's own key-up is untouched: a late release still fires at the
    /// level being held.
    /// </summary>
    [Fact]
    public void APlayerKeyUpStillCommitsTheLevelBeingHeld()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Melee);
        controller.SetDesiredPower(0.5f);

        controller.PressAttack(AttackHeight.Medium);
        now = 0.8d;
        controller.ReleaseAttack();

        Assert.Equal(0.8f, sent[0].Power, 3);
    }

    /// <summary>
    /// Mutation: leave the request in progress on an abort and the bar starts
    /// loading again by itself, firing a swing nobody asked for, and every
    /// request until then is refused behind it.
    /// </summary>
    [Fact]
    public void AbortingEndsTheRequestInsteadOfLeavingTheBarLoading()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Melee);
        controller.AutomationControlled = true;
        controller.SetDesiredPower(1f);
        controller.PressAttack(AttackHeight.Medium);
        Assert.True(controller.AttackRequestInProgress);

        controller.AbortAutomaticAttack();

        Assert.False(controller.AttackRequestInProgress);
        Assert.False(controller.AttackServerResponsePending);
        Assert.False(controller.BuildInProgress);

        now = 3d;
        controller.Tick();
        now = 6d;
        controller.Tick();

        Assert.Empty(sent);
        Assert.False(controller.BuildInProgress);
    }

    /// <summary>
    /// Mutation: reset the bar on every answer and a request that is still
    /// being charged goes back to zero, so each swing pays the charge of the
    /// one before it a second time.
    /// </summary>
    [Fact]
    public void TheAnswerToAFinishedSwingDoesNotRestartAChargeInProgress()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Melee);
        controller.SetDesiredPower(1f);
        controller.PressAttack(AttackHeight.Medium);

        now = 0.6d;
        controller.Tick();
        Assert.Equal(0.6f, controller.PowerBarLevel, 3);

        // The server answers for an earlier swing while this one is loading.
        combat.OnAttackDone(1u, 0u);

        now = 1d;
        controller.Tick();
        Assert.Equal(1f, controller.PowerBarLevel, 3);
    }

    /// <summary>
    /// Mutation: park the commit behind the server for an owner that names its
    /// own targets and the swing fires whenever the answer finally arrives, by
    /// then at whatever that owner has moved on to.
    /// </summary>
    [Fact]
    public void AnOwnerNamedReleaseOverABusyServerSaysNothingWentOut()
    {
        double now = 0d;
        var sent = new List<(AttackHeight Height, float Power)>();
        var combat = new CombatState();
        using var controller = Create(combat, () => now, sent);
        combat.SetCombatMode(CombatMode.Melee);
        controller.AutomationControlled = true;
        controller.SetDesiredPower(1f);
        controller.PressAttack(AttackHeight.Medium);
        combat.OnCombatCommenceAttack();
        Assert.True(controller.AttackServerResponsePending);

        now = 1d;
        Assert.False(controller.ReleaseAttack());
        Assert.False(controller.AttackRequestInProgress);
        Assert.Empty(sent);

        // The answer arrives long after the owner has moved on: no swing may
        // come out of it.
        combat.OnAttackDone(1u, 0u);
        now = 5d;
        controller.Tick();
        Assert.Empty(sent);
    }

    private static RuntimeCombatAttackState Create(
        CombatState combat,
        Func<double> now,
        List<(AttackHeight Height, float Power)> sent,
        Func<bool>? isDualWield = null,
        Func<bool>? autoRepeatAttack = null)
        => new(
            combat,
            canStartAttack: () => true,
            sendAttack: (height, power) =>
            {
                sent.Add((height, power));
                return true;
            },
            isDualWield: isDualWield,
            autoRepeatAttack: autoRepeatAttack ?? (() => false),
            now: now);
}
