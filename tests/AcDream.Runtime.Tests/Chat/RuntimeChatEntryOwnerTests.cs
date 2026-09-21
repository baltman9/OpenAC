using AcDream.Core.Chat;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Chat;

/// <summary>
/// The chat entry, which every front end that can be typed into drives.
/// Before this owner existed the draft, the recalled lines, the active
/// channel and the tell target lived in the windowed chat panel, so a console
/// could only ever say plain things to nobody in particular.
/// </summary>
public sealed class RuntimeChatEntryOwnerTests
{
    private sealed class RecordingBus : ICommandBus
    {
        internal List<object> Published { get; } = [];

        public void Publish<T>(T command) where T : notnull =>
            Published.Add(command);
    }

    private sealed class RecordingFeedback : IChatCommandFeedback
    {
        internal List<string> Lines { get; } = [];

        public string? LastIncomingTellSender { get; set; }

        public string? LastOutgoingTellTarget { get; set; }

        public void ShowInterfaceText(string text) => Lines.Add(text);

        public void ShowSystemMessage(string text) => Lines.Add(text);
    }

    // -- The draft ---------------------------------------------------------

    [Fact]
    public void SettingTheDraftToWhatItAlreadyIsTellsNobody()
    {
        var owner = new RuntimeChatEntryOwner();
        var seen = new List<string>();
        owner.DraftChanged += seen.Add;

        owner.SetDraft("hello");
        owner.SetDraft("hello");

        Assert.Equal(["hello"], seen);
        Assert.Equal("hello", owner.Draft);
    }

    // -- Composing ---------------------------------------------------------

    [Fact]
    public void ComposingStagesTheLineAndCountsAsTypingUntilItIsSent()
    {
        var owner = new RuntimeChatEntryOwner();

        Assert.False(owner.IsInputActive);
        Assert.True(owner.Compose("/vt nav"));

        Assert.Equal("/vt nav", owner.Draft);
        Assert.True(owner.IsInputActive);

        owner.Submit(null, new RecordingFeedback(), new RecordingBus());

        Assert.False(owner.IsInputActive);
        Assert.Equal(string.Empty, owner.Draft);
    }

    [Fact]
    public void ComposingIsRefusedWhileTheLineInTheEntryIsBeingTyped()
    {
        var owner = new RuntimeChatEntryOwner();
        owner.BindInputActiveSource(() => true);
        owner.SetDraft("half a sen");

        Assert.False(owner.Compose("something else"));
        Assert.Equal("half a sen", owner.Draft);
    }

    [Fact]
    public void ComposingIsRefusedWhenThereIsNoEntryToPutTheKeyboardIn()
    {
        var owner = new RuntimeChatEntryOwner();
        owner.BindEntryFocus(() => false);

        Assert.False(owner.Compose("hello"));
        Assert.Equal(string.Empty, owner.Draft);
    }

    // -- Sending -----------------------------------------------------------

    [Fact]
    public void SendingRoutesTheLineThroughTheActiveChannel()
    {
        var owner = new RuntimeChatEntryOwner();
        var bus = new RecordingBus();
        owner.SetChannel(ChatChannelKind.Fellowship);

        SubmitOutcome outcome =
            owner.Submit("group up", new RecordingFeedback(), bus);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        var sent = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal(ChatChannelKind.Fellowship, sent.Channel);
        Assert.Equal("group up", sent.Text);
    }

    [Fact]
    public void SendingAimsAtTheTellTargetTheEntryIsPointedAt()
    {
        var owner = new RuntimeChatEntryOwner();
        var bus = new RecordingBus();
        owner.SetTellTarget("Bob", 0x5000000Au);

        _ = owner.Submit("hi", new RecordingFeedback(), bus);

        var sent = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal(ChatChannelKind.Tell, sent.Channel);
        Assert.Equal("Bob", sent.TargetName);
        Assert.Equal(0x5000000Au, sent.TargetGuid);
    }

    [Fact]
    public void ChoosingAChannelDropsTheTellTargetBecauseTheyAreAlternatives()
    {
        var owner = new RuntimeChatEntryOwner();
        owner.SetTellTarget("Bob", 0x5000000Au);

        owner.SetChannel(ChatChannelKind.Trade);

        Assert.Null(owner.TellTarget);
        Assert.Equal(0u, owner.TellTargetGuid);
        Assert.Equal(ChatChannelKind.Trade, owner.ActiveChannel);
    }

    [Fact]
    public void SendingAnEmptyLineClearsTheEntryAndSendsNothing()
    {
        var owner = new RuntimeChatEntryOwner();
        var bus = new RecordingBus();
        owner.SetDraft("   ");

        Assert.Equal(
            SubmitOutcome.Empty,
            owner.Submit(null, new RecordingFeedback(), bus));
        Assert.Empty(bus.Published);
        Assert.Equal(string.Empty, owner.Draft);
        Assert.Equal(0, owner.Count);
    }

    [Fact]
    public void SendingWhileADraftIsPendingSendsWhatWasAskedForAndClearsTheDraft()
    {
        var owner = new RuntimeChatEntryOwner();
        var bus = new RecordingBus();
        Assert.True(owner.Compose("half a sentence"));

        _ = owner.Submit("hello", new RecordingFeedback(), bus);

        var sent = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal("hello", sent.Text);
        Assert.Equal(string.Empty, owner.Draft);
        Assert.False(owner.IsInputActive);
    }

    // -- What was sent before ---------------------------------------------

    [Fact]
    public void WalkingBackThroughSentLinesReachesTheOneBeforeLast()
    {
        var owner = new RuntimeChatEntryOwner();
        var bus = new RecordingBus();
        var feedback = new RecordingFeedback();
        _ = owner.Submit("first", feedback, bus);
        _ = owner.Submit("second", feedback, bus);
        _ = owner.Submit("third", feedback, bus);

        Assert.Equal("third", owner.RecallPrevious());
        Assert.Equal("second", owner.RecallPrevious());
        Assert.Equal("second", owner.Draft);

        _ = owner.Submit(null, feedback, bus);

        Assert.Equal(
            ["first", "second", "third", "second"],
            bus.Published.Cast<SendChatCmd>().Select(cmd => cmd.Text).ToArray());
    }

    [Fact]
    public void WalkingForwardPastTheNewestLineEmptiesTheEntry()
    {
        var owner = new RuntimeChatEntryOwner();
        owner.Remember("first");

        Assert.Equal("first", owner.RecallPrevious());
        Assert.Null(owner.RecallNext());
        Assert.Equal(string.Empty, owner.Draft);
        Assert.False(owner.IsRecalling);
    }

    [Fact]
    public void WalkingForwardWithoutWalkingBackFirstDoesNothing()
    {
        var owner = new RuntimeChatEntryOwner();
        owner.Remember("first");
        owner.SetDraft("half a sen");

        Assert.Null(owner.RecallNext());
        Assert.Equal("half a sen", owner.Draft);
    }

    [Fact]
    public void OnlyTheLastHundredSentLinesAreKept()
    {
        var owner = new RuntimeChatEntryOwner();
        for (int index = 0; index < RuntimeChatEntryOwner.HistoryLimit + 5; index++)
            owner.Remember($"line {index}");

        Assert.Equal(RuntimeChatEntryOwner.HistoryLimit, owner.Count);
        Assert.Equal(
            $"line {RuntimeChatEntryOwner.HistoryLimit + 4}",
            owner.RecallPrevious());
    }

    // -- The session owner supplies one ------------------------------------

    [Fact]
    public void TheCommunicationOwnerCarriesTheOneChatEntry()
    {
        using var state = new RuntimeCommunicationState();

        Assert.NotNull(state.ChatEntryOwner);
        state.ChatEntryOwner.SetDraft("staged");
        state.Dispose();

        Assert.Equal(string.Empty, state.ChatEntryOwner.Draft);
    }
}
