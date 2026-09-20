using AcDream.Runtime.Gameplay;
using AcDream.Core.Physics;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeApproachCompletionStateTests
{
    [Fact]
    public void CompletionMailbox_PreservesPublicationOrder()
    {
        var state = new RuntimeApproachCompletionState();
        IRuntimeApproachCompletionSink lifetime = state.BeginControllerLifetime();

        Assert.True(state.TryBeginApproach(out RuntimeInteractionApproachToken token));
        lifetime.PublishNaturalCompletion();
        lifetime.PublishCancellation(WeenieError.NoObject);

        Assert.True(state.TryTake(out RuntimeApproachCompletion natural));
        Assert.Equal(token, natural.Token);
        Assert.True(natural.IsNatural);
        Assert.Equal(WeenieError.None, natural.Error);
        Assert.True(state.TryTake(out RuntimeApproachCompletion cancellation));
        Assert.False(cancellation.IsNatural);
        Assert.Equal(WeenieError.NoObject, cancellation.Error);
        Assert.False(state.TryTake(out _));
    }

    [Fact]
    public void Clear_DiscardsEveryPendingCompletion()
    {
        var state = new RuntimeApproachCompletionState();
        IRuntimeApproachCompletionSink lifetime = state.BeginControllerLifetime();
        Assert.True(state.TryBeginApproach(out _));
        lifetime.PublishNaturalCompletion();
        lifetime.PublishCancellation(WeenieError.ActionCancelled);

        state.Clear();

        Assert.False(state.TryTake(out _));
    }

    [Fact]
    public void RetiredLifetime_CannotPublishIntoReplacementLifetime()
    {
        var state = new RuntimeApproachCompletionState();
        IRuntimeApproachCompletionSink stale = state.BeginControllerLifetime();
        Assert.True(state.TryBeginApproach(out RuntimeInteractionApproachToken staleToken));
        state.RetireControllerLifetime(stale);
        Assert.True(state.TryTake(out RuntimeApproachCompletion retirement));
        Assert.Equal(staleToken, retirement.Token);
        Assert.False(retirement.IsNatural);
        IRuntimeApproachCompletionSink current = state.BeginControllerLifetime();
        Assert.True(state.TryBeginApproach(out RuntimeInteractionApproachToken currentToken));

        stale.PublishNaturalCompletion();
        current.PublishNaturalCompletion();

        Assert.True(state.TryTake(out RuntimeApproachCompletion completion));
        Assert.Equal(currentToken, completion.Token);
        Assert.False(state.TryTake(out _));
    }

    [Fact]
    public void Retirement_ReplacesQueuedSuccessWithMatchingCancellation()
    {
        var state = new RuntimeApproachCompletionState();
        IRuntimeApproachCompletionSink lifetime = state.BeginControllerLifetime();
        Assert.True(state.TryBeginApproach(out RuntimeInteractionApproachToken token));
        lifetime.PublishNaturalCompletion();

        state.RetireControllerLifetime(lifetime);

        Assert.True(state.TryTake(out RuntimeApproachCompletion completion));
        Assert.Equal(token, completion.Token);
        Assert.False(completion.IsNatural);
        Assert.Equal(WeenieError.ActionCancelled, completion.Error);
        Assert.False(state.TryTake(out _));
    }
}
