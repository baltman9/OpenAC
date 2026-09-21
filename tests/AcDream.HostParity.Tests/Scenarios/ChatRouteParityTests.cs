using AcDream.Core.Chat;
using AcDream.Runtime.Chat;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Typing a line, on both clients, through each client's own route.
///
/// Until now the windowed arm hung an inert route where its real one goes --
/// its bindings could not be built without a presentation tree beside them --
/// so a typed line went nowhere on one arm and out through the real route on
/// the other, and nothing compared them. Both arms now build their own
/// client's route, and a line goes in the way a person's does: into the one
/// chat entry, submitted, and out through whatever the route makes of it.
///
/// What is written down for each line: what the route asked the connection to
/// send, byte for byte; every line that appeared in the chat feed with its
/// text, its kind and its place in the order; and what the submission itself
/// answered. Those three are what a plugin, a chat box and a console all read.
///
/// Mutation check (2026-09-20), run: making the windowless client's route
/// resolve no pose -- which is what it used to be given -- turned the pose
/// scenario red and left the other thirteen green. Restoring it turned it
/// green. The first attempt at that check passed under the mutation and was
/// therefore worthless: with no pose table read, both clients drop a pose
/// and agree about it, so the scenario now stages the same small table on
/// both arms before it says anything.
/// </summary>
public sealed class ChatRouteParityTests
{
    [Fact]
    public void SayingSomethingGoesOutTheSameWayOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("say hello");
            // Said outright: something really went out. Two clients whose
            // routes were both inert would agree and prove nothing.
            Assert.NotEqual(0, Type(arm, transcript, "hello"));
        });

    [Fact]
    public void TellingOneListenerGoesOutTheSameWayOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("tell Bob");
            Assert.NotEqual(0, Type(arm, transcript, "@tell Bob, hi"));
        });

    /// <summary>
    /// A reply with nobody to reply to. The line is the client's own to
    /// answer, nothing may be sent, and the reason has to be put to the
    /// player in the same words on both clients.
    /// </summary>
    /// <remarks>
    /// The words are put in the client's own notice queue, which each front
    /// end empties onto the screen on its own clock -- a window's notice
    /// box, a console's own pump -- and neither of those exists here. So
    /// what is compared is the queue itself, not what has been shown out of
    /// it.
    /// </remarks>
    [Fact]
    public void AReplyWithNoOneToReplyToIsRefusedTheSameWayOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("reply to nobody");
            SubmitOutcome outcome = Entry(arm).Submit(
                "/r hi",
                new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
                arm.Commands);
            transcript.Record("outcome", outcome);
            int sent = arm.Operations.Outbound.Count;
            RecordFeed(arm, transcript);
            transcript.RecordOutbound(arm);
            RecordNotices(arm, transcript);
            // The client answered the line itself and sent nothing, and it
            // really did have something to say: two clients that both said
            // nothing would agree for nothing.
            Assert.Equal(SubmitOutcome.ClientHandled, outcome);
            Assert.Equal(0, sent);
            Assert.NotEmpty(arm.Runtime.CommunicationOwner.SpewBox.Snapshot());
        });

    /// <summary>
    /// A pose inside a line of speech: the words go to the server, the
    /// motion is played, the onlookers' line is sent and the speaker's own
    /// line is printed. A client that dropped poses said the words and
    /// nothing else.
    /// </summary>
    [Fact]
    public void APoseInsideALineOfSpeechRunsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            // A pose table, which a client with no data files has none of.
            // Both arms are given the same one, so it cannot hide a
            // difference -- and without it both clients drop the pose and
            // agree about having done nothing.
            arm.Runtime.CommunicationOwner.ChatPoses = ChatPoseCatalog.Of(
            [
                new("wave", new RetailChatPose(
                    MotionCommand: 0x1300_0026u,
                    SelfText: "You wave.",
                    OthersText: "waves %p hand.")),
            ]);

            transcript.Step("say hello with a wave");
            int sent = Type(arm, transcript, "hello *wave*");
            transcript.Record(
                "motion",
                arm.Runtime.MovementOwner.Controller?.Movement.Minterp
                    .InterpretedState.ForwardCommand ?? 0u);
            // Said outright: the pose really ran. The words went out, and
            // the speaker's own line about the pose is in the chat feed --
            // a client that dropped poses said the words and nothing else,
            // and two clients that both dropped them agree for nothing.
            Assert.NotEqual(0, sent);
            Assert.Contains(
                arm.Runtime.CommunicationOwner.ChatFeed.Snapshot(),
                line => line.Text.Contains(
                    "You wave", StringComparison.Ordinal));
        });

    /// <summary>
    /// A channel tag with no command behind it. Both clients have to fall
    /// back the same way rather than one sending and the other not.
    /// </summary>
    [Fact]
    public void AChannelTagWithNoCommandFallsBackTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("a channel tag");
            Type(arm, transcript, "@f general x");
            RecordInterfaceText(arm, transcript);
        });

    /// <summary>
    /// Staging a line and then sending it, which is what a plugin does when
    /// it puts words in the box for the player to look at first.
    /// </summary>
    [Fact]
    public void StagingALineThenSendingItRunsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("stage it");
            transcript.Record(
                "accepted", arm.Host.Automation.Chat.Compose("x"));
            transcript.Record("draft", Entry(arm).Draft);
            transcript.Record(
                "inputActive", arm.Host.Automation.Chat.IsInputActive);

            transcript.Step("send what is staged");
            Submit(arm, transcript, null);
            RecordFeed(arm, transcript);
            int sent = arm.Operations.Outbound.Count;
            transcript.RecordOutbound(arm);
            transcript.Record("draft", Entry(arm).Draft);
            Assert.NotEqual(0, sent);
        });

    /// <summary>
    /// A line submitted while something else is staged. The staged words are
    /// not what goes out, and what happens to them has to be the same on
    /// both clients.
    /// </summary>
    [Fact]
    public void SendingWhileSomethingIsStagedRunsTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("stage half a sentence");
            transcript.Record(
                "accepted",
                arm.Host.Automation.Chat.Compose("half a sentence"));

            transcript.Step("send something else");
            Submit(arm, transcript, "hello");
            RecordFeed(arm, transcript);
            transcript.RecordOutbound(arm);
            transcript.Record("draft", Entry(arm).Draft);

            transcript.Step("stage again");
            transcript.Record(
                "accepted", arm.Host.Automation.Chat.Compose("second"));
            transcript.Record("draft", Entry(arm).Draft);
        });

    /// <summary>
    /// Three lines sent, the second recalled and sent again. What comes back
    /// out of the history is the same on both clients, and so is the second
    /// send of it.
    /// </summary>
    [Fact]
    public void RecallingAnEarlierLineSendsItAgainTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("send three");
            foreach (string line in new[] { "first", "second", "third" })
                Submit(arm, transcript, line);
            transcript.Record("remembered", Entry(arm).Count);
            _ = arm.Operations.TakeOutbound();

            transcript.Step("recall the second");
            transcript.Record("back1", Entry(arm).RecallPrevious());
            transcript.Record("back2", Entry(arm).RecallPrevious());
            transcript.Record("draft", Entry(arm).Draft);

            transcript.Step("send it again");
            Submit(arm, transcript, null);
            int sent = arm.Operations.Outbound.Count;
            transcript.RecordOutbound(arm);
            Assert.NotEqual(0, sent);
        });

    /// <summary>
    /// A line the server says, and a reader who only subscribes afterwards.
    /// The whole feed comes back with the same places in the order, so a
    /// plugin loaded half way through a session reads the same history as one
    /// loaded at the start.
    /// </summary>
    [Fact]
    public void AFeedSubscribedToLateReplaysTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("the server says three things");
            for (int index = 0; index < 3; index++)
            {
                arm.Server.SystemMessage(
                    $"line {index}", chatType: (uint)RetailLogTextType.System);
            }
            arm.Advance();

            transcript.Step("subscribe now and read it all back");
            RecordFeed(arm, transcript);
            // The feed really has something in it to replay.
            Assert.NotEmpty(arm.Runtime.CommunicationOwner.ChatFeed.Snapshot());
        });

    /// <summary>
    /// A line whose kind the chat window is set not to show. It has to be
    /// absent from the window on both clients, not shown on one of them.
    /// </summary>
    [Fact]
    public void ALineFilteredOutOfTheWindowIsShownOnNeitherClient() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("turn one kind off");
            ChatWindowState windows =
                arm.Runtime.CommunicationOwner.ChatWindows;
            ulong before = windows.GetFilter(ChatWindowState.MainWindowId);
            windows.SetFilter(
                ChatWindowState.MainWindowId,
                before & ~(1UL << (int)RetailLogTextType.Allegiance));
            transcript.Record(
                "filtered",
                !windows.TypeIsActive(
                    ChatWindowState.MainWindowId,
                    (uint)RetailLogTextType.Allegiance));

            transcript.Step("the server says one of each");
            arm.Server.SystemMessage(
                "kept", chatType: (uint)RetailLogTextType.System);
            arm.Server.SystemMessage(
                "dropped", chatType: (uint)RetailLogTextType.Allegiance);
            arm.Advance();
            RecordWindow(arm, transcript);
            // Both clients showing nothing at all would agree, so insist the
            // kept line really is shown while the other really is not.
            IReadOnlyList<RuntimeChatLine> shown = Shown(arm);
            Assert.Contains(shown, line => line.Text.Contains("kept"));
            Assert.DoesNotContain(shown, line => line.Text.Contains("dropped"));
        });

    /// <summary>
    /// The client's own verbs, typed. Each one either sends something or
    /// answers in words, on both clients, and no line may throw at whoever
    /// typed it.
    /// </summary>
    [Theory]
    [InlineData("/status")]
    [InlineData("/nav status")]
    [InlineData("/nav grid")]
    [InlineData("/motor turn left 90")]
    public void APluginVerbTypedAtTheBoxAnswersTheSameOnBothClients(
        string line) =>
        ParityScenario.Run((arm, transcript) =>
        {
            transcript.Step($"type {line}");
            Type(arm, transcript, line);
            RecordInterfaceText(arm, transcript);
        });

    private static RuntimeChatEntryOwner Entry(ParityArm arm) =>
        arm.Runtime.CommunicationOwner.ChatEntryOwner;

    /// <summary>
    /// Types one line and submits it the way a person does: through the one
    /// chat entry, onto this client's own outbound route.
    /// </summary>
    /// <summary>
    /// Types one line, and answers how many messages the route asked to
    /// send. The count is read before the outbound record is taken, because
    /// taking it empties it -- an assertion asked afterwards is worth
    /// nothing.
    /// </summary>
    private static int Type(
        ParityArm arm, ParityTranscript transcript, string line)
    {
        Submit(arm, transcript, line);
        RecordFeed(arm, transcript);
        int sent = arm.Operations.Outbound.Count;
        transcript.RecordOutbound(arm);
        return sent;
    }

    private static void Submit(
        ParityArm arm, ParityTranscript transcript, string? line)
    {
        transcript.Record(
            "outcome",
            Entry(arm).Submit(
                line,
                new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
                arm.Commands));
    }

    /// <summary>
    /// Everything in the chat feed, with the text, the kind and the place in
    /// the order a reader is given. A chat box and a console both read this,
    /// so two clients writing different text into it is a difference a player
    /// sees.
    /// </summary>
    private static void RecordFeed(ParityArm arm, ParityTranscript transcript)
    {
        IReadOnlyList<RuntimeChatLine> lines =
            arm.Runtime.CommunicationOwner.ChatFeed.Snapshot();
        transcript.Record("feed.count", lines.Count);
        // The place in the order, not the running number the client stamps.
        // Both clients write the same in-world lines in the same order; the
        // number they start from depends on what each client said about the
        // session opening before the character was in, and the two say that
        // in different places -- the one with a window in the chat log the
        // player reads, the one without on its console. What a reader is
        // handed is the order, so the order is what is compared.
        long previous = long.MinValue;
        for (int index = 0; index < lines.Count; index++)
        {
            RuntimeChatLine line = lines[index];
            transcript.Record(
                $"feed[{index}].afterThePreviousLine",
                line.Sequence > previous);
            previous = line.Sequence;
            transcript.Record($"feed[{index}].kind", line.Kind.ToString());
            transcript.Record($"feed[{index}].logText", line.LogTextType.ToString());
            transcript.Record($"feed[{index}].text", line.Text);
        }
    }

    /// <summary>The lines the main chat window really shows.</summary>
    private static IReadOnlyList<RuntimeChatLine> Shown(ParityArm arm) =>
        arm.Runtime.CommunicationOwner.ChatFeed.SnapshotForWindow(
            ChatWindowState.MainWindowId);

    private static void RecordWindow(ParityArm arm, ParityTranscript transcript)
    {
        IReadOnlyList<RuntimeChatLine> shown = Shown(arm);
        transcript.Record("shown.count", shown.Count);
        for (int index = 0; index < shown.Count; index++)
            transcript.Record($"shown[{index}].text", shown[index].Text);
    }

    /// <summary>
    /// What the client has queued to put to the player in its own words,
    /// which is the part both clients share; emptying the queue onto a
    /// screen belongs to each front end.
    /// </summary>
    private static void RecordNotices(ParityArm arm, ParityTranscript transcript)
    {
        // Each front end empties the queue onto its own screen on its own
        // clock, and neither of those exists here, so the harness empties it
        // once -- the same way on both arms -- and compares what came out.
        arm.Runtime.CommunicationOwner.SpewBox.Tick(
            arm.Runtime.Clock.SimulationTimeSeconds);
        RecordInterfaceText(arm, transcript);
    }

    /// <summary>What the client said to the player in its own words.</summary>
    private static void RecordInterfaceText(
        ParityArm arm, ParityTranscript transcript)
    {
        IReadOnlyList<SpewBoxEntry> lines =
            arm.Runtime.CommunicationOwner.SpewBox.Snapshot();
        transcript.Record("interface.count", lines.Count);
        for (int index = 0; index < lines.Count; index++)
            transcript.Record($"interface[{index}]", lines[index].ToString());
    }
}
