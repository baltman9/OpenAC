using AcDream.Core.Items;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

/// <summary>
/// The one asking slot every description shares. A server that never answers
/// one object must not take the channel away from every later question: the
/// wait ends by itself, the asker is told its question failed, and the next
/// request goes out.
/// </summary>
public sealed class RuntimeAppraisalChannelExpiryTests
{
    private const uint Silent = 0x80016800u;
    private const uint Next = 0x80016801u;

    [Fact]
    public void AnUnansweredDescriptionFreesTheChannelWithinItsBound()
    {
        long now = 10_000L;
        using var inventory = NewInventory();
        using var state = new RuntimeInteractionTransactionState(
            inventory,
            () => now);
        var sent = new List<uint>();

        Assert.True(state.TryRequestAppraisal(
            Silent,
            sent.Add,
            AppraisalRequestOrigin.Automation));
        Assert.Equal([Silent], sent);

        // One turn short of the bound the wait still stands: an answer on
        // its way must not be thrown away.
        now += RuntimeInteractionTransactionState.AppraisalRequestTimeoutMs - 1;
        Assert.False(state.CanBeginAppraisal);
        Assert.False(state.IsAwaitingAppraisalExpired);
        Assert.Equal(1, inventory.AppraisalCount);

        now += 1;
        Assert.True(state.IsAwaitingAppraisalExpired);
        Assert.True(state.CanBeginAppraisal);

        Assert.True(state.TryRequestAppraisal(
            Next,
            sent.Add,
            AppraisalRequestOrigin.Automation));
        Assert.Equal([Silent, Next], sent);
        Assert.Equal(Next, state.AwaitingAppraisalId);
        Assert.Equal(Silent, state.LastAbandonedAppraisalId);
        Assert.Equal(1, inventory.AppraisalCount);
    }

    [Fact]
    public void TheAskerIsToldItsQuestionFailed()
    {
        long now = 10_000L;
        using var inventory = NewInventory();
        using var state = new RuntimeInteractionTransactionState(
            inventory,
            () => now);
        var abandoned = new List<uint>();
        state.AppraisalAbandoned += abandoned.Add;

        Assert.True(state.TryRequestAppraisal(
            Silent,
            _ => { },
            AppraisalRequestOrigin.Automation));
        now += RuntimeInteractionTransactionState.AppraisalRequestTimeoutMs;

        Assert.True(state.TryExpireAwaitingAppraisal());
        Assert.Equal([Silent], abandoned);
        Assert.Equal(Silent, state.LastAbandonedAppraisalId);
        Assert.Equal(0u, state.AwaitingAppraisalId);
        Assert.Equal(0, inventory.AppraisalCount);

        // Nothing to expire twice: the give-up is reported once.
        Assert.False(state.TryExpireAwaitingAppraisal());
        Assert.Equal([Silent], abandoned);
    }

    [Fact]
    public void AskingTheSameObjectAgainClearsItsOwnFailureSignal()
    {
        long now = 10_000L;
        using var inventory = NewInventory();
        using var state = new RuntimeInteractionTransactionState(
            inventory,
            () => now);

        Assert.True(state.TryRequestAppraisal(
            Silent,
            _ => { },
            AppraisalRequestOrigin.Automation));
        now += RuntimeInteractionTransactionState.AppraisalRequestTimeoutMs;
        Assert.True(state.TryExpireAwaitingAppraisal());
        Assert.Equal(Silent, state.LastAbandonedAppraisalId);

        Assert.True(state.TryRequestAppraisal(
            Silent,
            _ => { },
            AppraisalRequestOrigin.Automation));
        Assert.Equal(0u, state.LastAbandonedAppraisalId);
    }

    [Fact]
    public void ANeverAnsweringObjectDoesNotStarveLaterAskers()
    {
        long now = 10_000L;
        using var inventory = NewInventory();
        using var state = new RuntimeInteractionTransactionState(
            inventory,
            () => now);
        var sent = new List<uint>();

        Assert.True(state.TryRequestAppraisal(
            Silent,
            sent.Add,
            AppraisalRequestOrigin.Automation));

        // Three later askers, each one bound apart, all get their turn.
        for (uint asker = 1u; asker <= 3u; asker++)
        {
            now += RuntimeInteractionTransactionState.AppraisalRequestTimeoutMs;
            Assert.True(state.TryRequestAppraisal(
                Next + asker,
                sent.Add,
                AppraisalRequestOrigin.Automation));
        }

        Assert.Equal([Silent, Next + 1u, Next + 2u, Next + 3u], sent);
        Assert.Equal(1, inventory.AppraisalCount);
    }

    [Fact]
    public void AWaitStillOnItsWayIsNotGivenUpOn()
    {
        long now = 10_000L;
        using var inventory = NewInventory();
        using var state = new RuntimeInteractionTransactionState(
            inventory,
            () => now);
        var sent = new List<uint>();

        Assert.True(state.TryRequestAppraisal(
            Silent,
            sent.Add,
            AppraisalRequestOrigin.Automation));
        now += RuntimeInteractionTransactionState.AppraisalRequestIntervalMs;

        // The gate every asker goes through still refuses, and nothing is
        // given up: the answer is still on its way.
        Assert.False(state.CanBeginAppraisal);
        Assert.False(state.TryExpireAwaitingAppraisal());
        Assert.Equal(Silent, state.AwaitingAppraisalId);
        Assert.Equal(0u, state.LastAbandonedAppraisalId);
        Assert.Equal(1, inventory.AppraisalCount);
        Assert.Equal([Silent], sent);

        Assert.True(state.AcceptAppraisalResponse(Silent).FirstResponse);
        Assert.Equal(0, inventory.AppraisalCount);
        Assert.Equal(0u, state.LastAbandonedAppraisalId);
    }

    [Fact]
    public void TakingTheSlotForASpellTellsTheDisplacedAsker()
    {
        long now = 10_000L;
        using var inventory = NewInventory();
        using var state = new RuntimeInteractionTransactionState(
            inventory,
            () => now);
        var abandoned = new List<uint>();
        state.AppraisalAbandoned += abandoned.Add;

        Assert.True(state.TryRequestAppraisal(
            Silent,
            _ => { },
            AppraisalRequestOrigin.Automation));
        Assert.True(state.CancelObjectAppraisalForSpell(_ => { }));

        Assert.Equal([Silent], abandoned);
        Assert.Equal(Silent, state.LastAbandonedAppraisalId);
        Assert.Equal(0, inventory.AppraisalCount);
    }

    private static InventoryTransactionState NewInventory() =>
        new(new ClientObjectTable());
}
