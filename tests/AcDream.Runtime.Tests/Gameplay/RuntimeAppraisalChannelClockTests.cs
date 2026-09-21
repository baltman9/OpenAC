using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// The description channel and the item-use pacing sit under one throttle, so
/// they have to be stamped from one clock. Before this pin the give-up bound
/// was measured against the machine's uptime while the pacing beside it was
/// measured against the simulation clock, so a client whose simulation was
/// paused, stepped or run faster than real time gave up on descriptions at a
/// pace nothing else in the client shared.
/// </summary>
public sealed class RuntimeAppraisalChannelClockTests
{
    private const uint Silent = 0x80016900u;

    private static GameRuntime Create() =>
        RuntimeCreatureDeathStateTests.Create();

    [Fact]
    public void TheGiveUpBoundElapsesWithSimulatedTimeRatherThanWallTime()
    {
        using GameRuntime runtime = Create();
        RuntimeInteractionTransactionState transactions =
            runtime.ActionOwner.Transactions;
        var sent = new List<uint>();

        Assert.True(transactions.TryRequestAppraisal(
            Silent,
            sent.Add,
            AppraisalRequestOrigin.Automation));
        Assert.Equal([Silent], sent);
        Assert.False(transactions.IsAwaitingAppraisalExpired);

        // One turn short of the bound the wait still stands.
        double bound =
            RuntimeInteractionTransactionState.AppraisalRequestTimeoutMs
            / 1000d;
        runtime.Clock.Advance(bound - 0.002d);
        Assert.False(transactions.IsAwaitingAppraisalExpired);
        Assert.False(transactions.CanBeginAppraisal);

        // Advancing the simulation past the bound -- and nothing else -- ends
        // the wait. A wall clock would still be a few milliseconds into it.
        runtime.Clock.Advance(0.004d);
        Assert.True(transactions.IsAwaitingAppraisalExpired);
        Assert.True(transactions.CanBeginAppraisal);
    }

    [Fact]
    public void AStandingSimulationNeverGivesUpOnItsOwn()
    {
        using GameRuntime runtime = Create();
        RuntimeInteractionTransactionState transactions =
            runtime.ActionOwner.Transactions;

        Assert.True(transactions.TryRequestAppraisal(
            Silent,
            static _ => { },
            AppraisalRequestOrigin.Automation));

        // Real time passes while the simulation does not; the bound belongs to
        // the simulation, so the wait is untouched.
        Thread.Sleep(20);
        Assert.False(transactions.IsAwaitingAppraisalExpired);
        Assert.Equal(Silent, transactions.AwaitingAppraisalId);
    }
}
