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

    private static RuntimeChatEntryOwner Entry(ParityArm arm) =>
        arm.Runtime.CommunicationOwner.ChatEntryOwner;

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

            transcript.Step("compose");
            transcript.Record("accepted", chat.Compose("hello"));
            transcript.Record("draft", Entry(arm).Draft);
            transcript.Record("inputActive", chat.IsInputActive);

            transcript.Step("submit");
            var bus = new RecordingBus();
            transcript.Record(
                "outcome",
                Entry(arm).Submit(
                    null,
                    new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
                    bus));
            transcript.Record("draft", Entry(arm).Draft);
            transcript.Record("inputActive", chat.IsInputActive);
            RecordSends(transcript, bus);
        });

    [Fact]
    public void SendingWhileADraftIsPendingBehavesTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            IPluginChat chat = arm.Host.Automation.Chat;
            var bus = new RecordingBus();

            transcript.Step("compose");
            transcript.Record("accepted", chat.Compose("half a sentence"));

            transcript.Step("submit-something-else");
            transcript.Record(
                "outcome",
                Entry(arm).Submit(
                    "hello",
                    new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
                    bus));
            transcript.Record("draft", Entry(arm).Draft);
            RecordSends(transcript, bus);

            transcript.Step("compose-again");
            // The pending draft is gone, so staging is allowed again.
            transcript.Record("accepted", chat.Compose("second"));
            transcript.Record("draft", Entry(arm).Draft);
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
                transcript.Record(line, entry.Submit(line, feedback, bus));
            transcript.Record("remembered", entry.Count);

            transcript.Step("recall");
            transcript.Record("back1", entry.RecallPrevious());
            transcript.Record("back2", entry.RecallPrevious());
            transcript.Record("draft", entry.Draft);

            transcript.Step("send-the-recalled-line");
            transcript.Record("outcome", entry.Submit(null, feedback, bus));
            RecordSends(transcript, bus);
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

            transcript.Step("to-a-channel");
            entry.SetChannel(ChatChannelKind.Fellowship);
            transcript.Record("channel", entry.ActiveChannel);
            transcript.Record("tellTarget", entry.TellTarget ?? "<none>");
            transcript.Record("outcome", entry.Submit("group up", feedback, bus));

            transcript.Step("to-one-listener");
            entry.SetTellTarget("Bob", 0x5000000Au);
            transcript.Record("channel", entry.ActiveChannel);
            transcript.Record("tellTarget", entry.TellTarget ?? "<none>");
            transcript.Record("tellTargetGuid", entry.TellTargetGuid);
            transcript.Record("outcome", entry.Submit("hi", feedback, bus));

            transcript.Step("sent");
            RecordSends(transcript, bus);
        });

    [Fact]
    public void AReplyWithNoOneToReplyToIsRefusedTheSameWayOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var bus = new RecordingBus();
            RuntimeChatEntryOwner entry = Entry(arm);

            transcript.Step("reply");
            transcript.Record(
                "outcome",
                entry.Submit(
                    "/r hi",
                    new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
                    bus));
            RecordSends(transcript, bus);

            transcript.Step("what-the-player-reads");
            foreach (var line in arm.Runtime.CommunicationOwner.SpewBox
                .Snapshot())
            {
                transcript.Record("interfaceText", line);
            }
        });

    [Fact]
    public void AChannelTagWithNoCommandBehindItFallsBackTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            var bus = new RecordingBus();
            RuntimeChatEntryOwner entry = Entry(arm);

            transcript.Step("channel-tag");
            transcript.Record(
                "outcome",
                entry.Submit(
                    "@f general x",
                    new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
                    bus));
            RecordSends(transcript, bus);
        });
}
