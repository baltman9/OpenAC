using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Chat;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The chat entry through both clients: the draft, whether the keyboard is
/// going into it, what was sent before it, and where a plain line goes.
///
/// Before the entry became a runtime owner, only the windowed client had one:
/// <c>Compose</c> and <c>IsInputActive</c> were windowed-only seams with two
/// allow-list rows excusing them, and a plugin asking a windowless client to
/// stage a line got false. These comparisons would have AGREED back then, on
/// a pair of clients that both did nothing, so the fact that pins the change
/// is the last one here: a client with no window now really stages the line.
///
/// Every step is recorded AND asserted, on the draft, the answer and the
/// line that was published: a comparison of two transcripts passes whenever
/// the clients agree, including when they agree on staging nothing and
/// sending nothing.
///
/// Mutation check (2026-09-21), run: making a submitted line lose to whatever
/// was already staged in the box turned
/// <see cref="SendingWhileADraftIsPendingBehavesTheSameOnBothClients"/> red on
/// both arms at once -- "half a sentence" went out where "hello" should have.
///
/// What is and is not under test here: the entry, its routing and both hosts'
/// real binding pass are. The windowed chat PANEL is not -- it needs a layout
/// tree, a font and a sprite resolver, none of which exist without a window --
/// so the windowed arm drives the same entry the panel drives, through the
/// same plugin seam the panel's host binds. The panel's own delegation to the
/// entry is pinned separately, where a layout can be imported.
/// </summary>
public sealed class ChatEntryParityTests
{
    /// <summary>A bus that keeps what was published instead of sending it.</summary>
    private sealed class RecordingBus : ICommandBus
    {
        internal List<object> Published { get; } = [];

        public void Publish<T>(T command) where T : notnull =>
            Published.Add(command);
    }

    /// <summary>The one listener a tell in these scenarios names.</summary>
    private const uint Listener = 0x5000000Au;

    private static RuntimeChatEntryOwner Entry(ParityArm arm) =>
        arm.Runtime.CommunicationOwner.ChatEntryOwner;

    /// <summary>
    /// That exactly one line was published, on the channel and with the
    /// words expected. Two clients that both published nothing agree line
    /// for line, so this is said outright per arm.
    /// </summary>
    private static void AssertOneLine(
        RecordingBus bus, ChatChannelKind channel, string text)
    {
        var line = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal(channel, line.Channel);
        Assert.Equal(text, line.Text);
    }

    private static void RecordSends(
        ParityTranscript transcript, RecordingBus bus)
    {
        transcript.Record("sends", bus.Published.Count);
        for (int index = 0; index < bus.Published.Count; index++)
            transcript.Record($"send{index}", bus.Published[index].ToString());
    }

    /// <summary>
    /// Red before the chat entry moved into the runtime: a client with no
    /// window had no entry to stage a line in, so this answered false and the
    /// draft stayed empty. Both allow-list rows that excused it are gone.
    /// Mutation check (2026-09-20): binding the composer seam back to a
    /// refusal turned this red and restoring it turned it green.
    /// </summary>
    [Fact]
    public void AClientWithNoWindowStagesTheLineForReal()
    {
        using var arm = new WindowlessArm();
        arm.EnterWorld();
        IPluginChat chat = arm.Host.Automation.Chat;

        Assert.False(chat.IsInputActive);
        Assert.True(chat.Compose("hello"));
        Assert.True(chat.IsInputActive);
        Assert.Equal("hello", Entry(arm).Draft);
    }

    [Fact]
    public void StagingALineWithoutSendingItWorksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IPluginChat chat = arm.Host.Automation.Chat;

            transcript.Step("before");
            transcript.Record("inputActive", chat.IsInputActive);
            Assert.False(chat.IsInputActive);

            transcript.Step("compose");
            bool accepted = chat.Compose("hello");
            transcript.Record("accepted", accepted);
            transcript.Record("draft", Entry(arm).Draft);
            transcript.Record("inputActive", chat.IsInputActive);
            // The words are really in the box, and the box is taking the
            // keyboard. A client that staged nothing answers false here, and
            // two clients that both staged nothing agree.
            Assert.True(accepted, $"{arm.Name} would not stage the line.");
            Assert.Equal("hello", Entry(arm).Draft);
            Assert.True(chat.IsInputActive);

            transcript.Step("submit");
            var bus = new RecordingBus();
            SubmitOutcome outcome = Entry(arm).Submit(
                null,
                new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
                bus);
            transcript.Record("outcome", outcome);
            transcript.Record("draft", Entry(arm).Draft);
            transcript.Record("inputActive", chat.IsInputActive);
            RecordSends(transcript, bus);
            // What was staged is what went out, the box is empty again and
            // has given the keyboard back.
            Assert.Equal(SubmitOutcome.Sent, outcome);
            AssertOneLine(bus, ChatChannelKind.Say, "hello");
            Assert.Equal(string.Empty, Entry(arm).Draft);
            Assert.False(chat.IsInputActive);
        });

    [Fact]
    public void SendingWhileADraftIsPendingBehavesTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IPluginChat chat = arm.Host.Automation.Chat;
            var bus = new RecordingBus();

            transcript.Step("compose");
            bool staged = chat.Compose("half a sentence");
            transcript.Record("accepted", staged);
            Assert.True(staged, $"{arm.Name} would not stage the line.");

            transcript.Step("submit-something-else");
            SubmitOutcome outcome = Entry(arm).Submit(
                "hello",
                new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
                bus);
            transcript.Record("outcome", outcome);
            transcript.Record("draft", Entry(arm).Draft);
            RecordSends(transcript, bus);
            // The line that was submitted is what goes out -- not the half
            // sentence sitting in the box -- and the box is emptied.
            Assert.Equal(SubmitOutcome.Sent, outcome);
            AssertOneLine(bus, ChatChannelKind.Say, "hello");
            Assert.Equal(string.Empty, Entry(arm).Draft);

            transcript.Step("compose-again");
            // The pending draft is gone, so staging is allowed again.
            bool again = chat.Compose("second");
            transcript.Record("accepted", again);
            transcript.Record("draft", Entry(arm).Draft);
            Assert.True(again, $"{arm.Name} would not stage a second line.");
            Assert.Equal("second", Entry(arm).Draft);
        });

    [Fact]
    public void RecallingAnEarlierLineWorksTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var bus = new RecordingBus();
            var feedback =
                new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner);
            RuntimeChatEntryOwner entry = Entry(arm);

            transcript.Step("send-three");
            foreach (string line in new[] { "first", "second", "third" })
            {
                SubmitOutcome sent = entry.Submit(line, feedback, bus);
                transcript.Record(line, sent);
                Assert.Equal(SubmitOutcome.Sent, sent);
            }
            transcript.Record("remembered", entry.Count);
            Assert.Equal(3, entry.Count);

            transcript.Step("recall");
            string? back1 = entry.RecallPrevious();
            string? back2 = entry.RecallPrevious();
            transcript.Record("back1", back1);
            transcript.Record("back2", back2);
            transcript.Record("draft", entry.Draft);
            // Backwards through what was said, latest first, and the second
            // step back is in the box ready to go again.
            Assert.Equal("third", back1);
            Assert.Equal("second", back2);
            Assert.Equal("second", entry.Draft);

            transcript.Step("send-the-recalled-line");
            SubmitOutcome resent = entry.Submit(null, feedback, bus);
            transcript.Record("outcome", resent);
            RecordSends(transcript, bus);
            // Four lines went out, and the fourth is the recalled one.
            Assert.Equal(SubmitOutcome.Sent, resent);
            Assert.Equal(
                new[] { "first", "second", "third", "second" },
                bus.Published.Cast<SendChatCmd>()
                    .Select(static line => line.Text));
        });

    [Fact]
    public void WhereAPlainLineGoesIsDecidedTheSameWayOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var bus = new RecordingBus();
            var feedback =
                new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner);
            RuntimeChatEntryOwner entry = Entry(arm);

            transcript.Step("say");
            transcript.Record("channel", entry.ActiveChannel);
            transcript.Record("outcome", entry.Submit("hello", feedback, bus));
            // Nothing has been chosen, so a plain line is spoken aloud.
            Assert.Equal(ChatChannelKind.Say, entry.ActiveChannel);

            transcript.Step("to-a-channel");
            entry.SetChannel(ChatChannelKind.Fellowship);
            transcript.Record("channel", entry.ActiveChannel);
            transcript.Record("tellTarget", entry.TellTarget ?? "<none>");
            transcript.Record("outcome", entry.Submit("group up", feedback, bus));
            Assert.Equal(ChatChannelKind.Fellowship, entry.ActiveChannel);

            transcript.Step("to-one-listener");
            entry.SetTellTarget("Bob", Listener);
            transcript.Record("channel", entry.ActiveChannel);
            transcript.Record("tellTarget", entry.TellTarget ?? "<none>");
            transcript.Record("tellTargetGuid", entry.TellTargetGuid);
            transcript.Record("outcome", entry.Submit("hi", feedback, bus));
            // Naming one listener moves the box onto tells and remembers
            // who by name AND by guid.
            Assert.Equal(ChatChannelKind.Tell, entry.ActiveChannel);
            Assert.Equal("Bob", entry.TellTarget);
            Assert.Equal(Listener, entry.TellTargetGuid);

            transcript.Step("sent");
            RecordSends(transcript, bus);
            // Three lines, each on the channel the box was on when it was
            // submitted, and the tell carrying the listener.
            SendChatCmd[] published = [.. bus.Published.Cast<SendChatCmd>()];
            Assert.Equal(3, published.Length);
            Assert.Equal(
                new[]
                {
                    (ChatChannelKind.Say, "hello", 0u),
                    (ChatChannelKind.Fellowship, "group up", 0u),
                    (ChatChannelKind.Tell, "hi", Listener),
                },
                published.Select(static line =>
                    (line.Channel, line.Text, line.TargetGuid)));
            Assert.Equal("Bob", published[2].TargetName);
        });

    [Fact]
    public void AReplyWithNoOneToReplyToIsRefusedTheSameWayOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var bus = new RecordingBus();
            RuntimeChatEntryOwner entry = Entry(arm);

            transcript.Step("reply");
            SubmitOutcome outcome = entry.Submit(
                "/r hi",
                new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
                bus);
            transcript.Record("outcome", outcome);
            RecordSends(transcript, bus);
            // The client answered the line itself and sent nothing.
            Assert.Equal(SubmitOutcome.ClientHandled, outcome);
            Assert.Empty(bus.Published);

            transcript.Step("what-the-player-reads");
            // Each front end empties the queue onto its own screen on its
            // own clock and neither of those exists here, so the harness
            // empties it once, the same way on both arms.
            arm.Runtime.CommunicationOwner.SpewBox.Tick(
                arm.Runtime.Clock.SimulationTimeSeconds);
            foreach (var line in arm.Runtime.CommunicationOwner.SpewBox
                .Snapshot())
            {
                transcript.Record("interfaceText", line);
            }
            // It really had a reason to give: two clients that both said
            // nothing to the player agree and leave a bot none the wiser.
            Assert.NotEmpty(arm.Runtime.CommunicationOwner.SpewBox.Snapshot());
        });

    [Fact]
    public void AChannelTagWithNoCommandBehindItFallsBackTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var bus = new RecordingBus();
            RuntimeChatEntryOwner entry = Entry(arm);

            transcript.Step("channel-tag");
            SubmitOutcome outcome = entry.Submit(
                "@f general x",
                new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
                bus);
            transcript.Record("outcome", outcome);
            RecordSends(transcript, bus);
            // The tag is read as the channel to speak on and the rest of
            // the line is what is said on it -- not swallowed, and not sent
            // with the tag still in front of it.
            Assert.Equal(SubmitOutcome.Sent, outcome);
            AssertOneLine(bus, ChatChannelKind.Fellowship, "general x");
        });
}
