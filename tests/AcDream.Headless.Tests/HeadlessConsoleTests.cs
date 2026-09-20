using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AcDream.Core.Chat;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;
using AcDream.Headless.Diagnostics;
using AcDream.Headless.Hosting;
using AcDream.Headless.Platform;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Chat;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

public sealed class HeadlessConsoleTests
{
    // ── HeadlessConsoleOptions (typed option resolution) ─────────────────

    [Theory]
    [InlineData(true, "0", false, true)]    // CLI flag always wins, even over env "0"
    [InlineData(false, "1", false, true)]   // env var "1" wins over terminal default
    [InlineData(false, "yes", false, true)]
    [InlineData(false, "0", true, false)]   // env var "0" disables even when stdin is a terminal
    [InlineData(false, "0", false, false)]  // env var "0" disables when stdin is redirected too
    [InlineData(false, null, true, true)]   // no flag/env -> terminal-shaped default (on)
    [InlineData(false, null, false, false)] // no flag/env -> terminal-shaped default (off)
    public void ResolvePrefersFlagThenEnvironmentThenTerminalDefault(
        bool commandLineFlag,
        string? environmentValue,
        bool standardInputIsTerminal,
        bool expected)
    {
        bool resolved = HeadlessConsoleOptions.Resolve(
            commandLineFlag,
            _ => environmentValue,
            standardInputIsTerminal);

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void CommandLineParsesTheBareConsoleFlag()
    {
        HeadlessCommandLine parsed = HeadlessCommandLine.Parse(
            ["run", "--config", "bot.json", "--console"]);

        Assert.True(parsed.ConsoleEnabled);
        Assert.Equal("bot.json", parsed.ConfigurationPath);
    }

    [Fact]
    public void CommandLineWithoutTheFlagDefaultsConsoleOff()
    {
        HeadlessCommandLine parsed = HeadlessCommandLine.Parse(
            ["run", "--config", "bot.json"]);

        Assert.False(parsed.ConsoleEnabled);
    }

    [Fact]
    public void ValidateModeRejectsTheConsoleFlag()
    {
        Assert.Throws<HeadlessCommandLineException>(() =>
            HeadlessCommandLine.Parse(
                ["validate", "--config", "bot.json", "--console"]));
    }

    // ── HeadlessConsoleInputReader: reader-thread/ordering ───────────────

    [Fact]
    public void LinesQueuedByTheReaderThreadDrainInOrderOnTheCallingThread()
    {
        using var input = new System.IO.StringReader(
            "one" + Environment.NewLine
            + "two" + Environment.NewLine
            + "three" + Environment.NewLine);
        using var reader = new HeadlessConsoleInputReader(input);

        Assert.True(
            reader.EndOfInput.Wait(TimeSpan.FromSeconds(5)),
            "the reader thread never reached EOF");

        int callingThread = Environment.CurrentManagedThreadId;
        var drained = new List<string>();
        while (reader.TryDequeue(out string line))
        {
            drained.Add(line);
            Assert.Equal(callingThread, Environment.CurrentManagedThreadId);
        }

        Assert.Equal(["one", "two", "three"], drained);
    }

    // ── HeadlessConsoleController: the chat entry's second front end ─────
    //
    // Before this the controller answered /quit and /status itself, before
    // the router saw either, and printed its own "not handled (Dropped)"
    // line for an outcome the shared feedback had already explained. It is
    // now a line pump: a typed line goes into the one chat entry, and what
    // the line did is said by the chat feed.

    /// <summary>
    /// A console session with a real chat entry behind it and a recording
    /// submit, so a test can see exactly what reached the entry.
    /// </summary>
    private sealed class ConsoleSessionFixture
    {
        internal ConsoleSessionFixture(
            string id, Func<string, bool>? claimsVerb = null)
        {
            Id = id;
            Binding = new HeadlessConsoleSession(
                id,
                Entry,
                line =>
                {
                    Submitted.Add(line);
                    if (Throw is { } failure)
                        throw failure;
                    return Outcome;
                },
                claimsVerb ?? (static _ => false));
        }

        internal string Id { get; }
        internal RuntimeChatEntryOwner Entry { get; } = new();
        internal HeadlessConsoleSession Binding { get; }
        internal List<string?> Submitted { get; } = [];
        internal SubmitOutcome Outcome { get; set; } = SubmitOutcome.Sent;
        internal Exception? Throw { get; set; }
    }

    [Fact]
    public void SubmitRunsOnTheDrainCallersThreadNeverTheReaderThread()
    {
        using var fixture = new ThreadIdRecordingTextReader(
            new System.IO.StringReader("hello" + Environment.NewLine));
        int? observedSubmitThreadId = null;
        var session = new ConsoleSessionFixture("alpha");
        var binding = new HeadlessConsoleSession(
            session.Id,
            session.Entry,
            _ =>
            {
                observedSubmitThreadId = Environment.CurrentManagedThreadId;
                return SubmitOutcome.Sent;
            },
            static _ => false);
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            fixture, TextWriter.Null, [binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        int drainCallerThreadId = Environment.CurrentManagedThreadId;
        controller.DrainDue();

        Assert.NotNull(fixture.ReadLineThreadId);
        Assert.NotNull(observedSubmitThreadId);
        Assert.NotEqual(fixture.ReadLineThreadId, observedSubmitThreadId);
        Assert.Equal(drainCallerThreadId, observedSubmitThreadId);
    }

    [Fact]
    public void ControllerDrainsEveryLineQueuedSinceTheLastTickInOrderOnOneCall()
    {
        using var input = new System.IO.StringReader(
            "alpha" + Environment.NewLine
            + "beta" + Environment.NewLine
            + "gamma" + Environment.NewLine);
        var session = new ConsoleSessionFixture("one");
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, TextWriter.Null, [session.Binding], quit);

        Assert.True(
            WaitForEndOfInput(controller),
            "the reader thread never reached EOF");
        controller.DrainDue();

        Assert.Equal(["alpha", "beta", "gamma"], session.Submitted);
        Assert.Equal(3, controller.LastDrainCount);

        // A second drain with nothing queued does nothing — proves DrainDue
        // does not re-process already-handled lines.
        controller.DrainDue();
        Assert.Equal(["alpha", "beta", "gamma"], session.Submitted);
        Assert.Equal(0, controller.LastDrainCount);
    }

    [Fact]
    public void QuitRequestsCancellationAndNeverReachesSubmit()
    {
        using var input = new System.IO.StringReader("/quit" + Environment.NewLine);
        var session = new ConsoleSessionFixture("one");
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, output, [session.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.True(quit.IsCancellationRequested);
        Assert.Empty(session.Submitted);
        Assert.Contains("quitting", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The console's own verbs are offered to the session's one command
    /// registry first, so a plugin that registered the same verb keeps it and
    /// the line goes to the router as any other line would.
    /// </summary>
    [Fact]
    public void AVerbAPluginAlreadyOwnsIsNeverShadowedByTheConsolesOwn()
    {
        using var input = new System.IO.StringReader("/quit" + Environment.NewLine);
        var session = new ConsoleSessionFixture(
            "one",
            claimsVerb: verb => verb == HeadlessConsoleController.QuitVerb);
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, TextWriter.Null, [session.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.False(quit.IsCancellationRequested);
        Assert.Equal(["/quit"], session.Submitted);
    }

    /// <summary>
    /// /status is a verb of the client's now, answered by the one registry,
    /// so the console hands it to the router like anything else and the chat
    /// box gets the same answer.
    /// </summary>
    [Fact]
    public void StatusGoesToTheRouterRatherThanBeingAnsweredByTheConsole()
    {
        using var input = new System.IO.StringReader("/status" + Environment.NewLine);
        var session = new ConsoleSessionFixture("one")
        {
            Outcome = SubmitOutcome.ClientHandled,
        };
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, output, [session.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.Equal(["/status"], session.Submitted);
        Assert.Equal(string.Empty, output.ToString());
    }

    /// <summary>
    /// An outcome is not a message. The router already told the player what
    /// happened through the shared feedback, which reaches the console as a
    /// line of the chat feed, so a second sentence here would be a divergence
    /// from the chat box rather than a courtesy.
    /// </summary>
    [Theory]
    [InlineData(SubmitOutcome.UnknownCommand)]
    [InlineData(SubmitOutcome.Dropped)]
    [InlineData(SubmitOutcome.Sent)]
    public void NothingIsPrintedAboutWhatTheLineDid(SubmitOutcome outcome)
    {
        using var input = new System.IO.StringReader("garbage" + Environment.NewLine);
        var session = new ConsoleSessionFixture("one") { Outcome = outcome };
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, output, [session.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void SubmitFailurePrintsALineAndNeverEscapesDrainDue()
    {
        using var input = new System.IO.StringReader("boom" + Environment.NewLine);
        var session = new ConsoleSessionFixture("one")
        {
            Throw = new InvalidOperationException("fixture failure"),
        };
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, output, [session.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.Contains("fixture failure", output.ToString());
    }

    /// <summary>
    /// Enter on an empty console line is Enter on an empty chat entry: it
    /// sends whatever is staged, which is how a line a plugin composed goes
    /// out. The staged line is shown first so the person can see it.
    /// </summary>
    [Fact]
    public void AStagedDraftIsShownAndTheNextEnterSendsIt()
    {
        using var input = new System.IO.StringReader(Environment.NewLine);
        var session = new ConsoleSessionFixture("one");
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, output, [session.Binding], quit);

        Assert.True(session.Entry.Compose("/vt start"));
        Assert.Contains("draft: /vt start", output.ToString());

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        // Null means "the draft as it stands", which is what pressing Enter
        // in a chat box sends.
        Assert.Equal([null], session.Submitted);
    }

    // ── Several sessions on one console ──────────────────────────────────

    [Fact]
    public void AnAddressedLineGoesToThatSessionAndLeavesTheDefaultAlone()
    {
        using var input = new System.IO.StringReader(
            "@beta hello beta" + Environment.NewLine
            + "hello alpha" + Environment.NewLine);
        var alpha = new ConsoleSessionFixture("alpha");
        var beta = new ConsoleSessionFixture("beta");
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, TextWriter.Null, [alpha.Binding, beta.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.Equal(["hello beta"], beta.Submitted);
        Assert.Equal(["hello alpha"], alpha.Submitted);
        Assert.Equal("alpha", controller.DefaultSessionId);
    }

    /// <summary>
    /// An at sign in front of something that is not a session goes to the
    /// router untouched, so the server verbs that start with one still work.
    /// </summary>
    [Fact]
    public void AnAtSignThatNamesNoSessionIsLeftForTheRouter()
    {
        using var input = new System.IO.StringReader(
            "@tell Bob, hi" + Environment.NewLine);
        var alpha = new ConsoleSessionFixture("alpha");
        var beta = new ConsoleSessionFixture("beta");
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, TextWriter.Null, [alpha.Binding, beta.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.Equal(["@tell Bob, hi"], alpha.Submitted);
        Assert.Empty(beta.Submitted);
    }

    [Fact]
    public void SessionSwitchesWhichSessionAnUnaddressedLineGoesTo()
    {
        using var input = new System.IO.StringReader(
            "/session beta" + Environment.NewLine
            + "hello" + Environment.NewLine
            + "/session nowhere" + Environment.NewLine);
        var alpha = new ConsoleSessionFixture("alpha");
        var beta = new ConsoleSessionFixture("beta");
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, output, [alpha.Binding, beta.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.Equal("beta", controller.DefaultSessionId);
        Assert.Equal(["hello"], beta.Submitted);
        Assert.Empty(alpha.Submitted);
        Assert.Contains("no session is called nowhere", output.ToString());
    }

    [Fact]
    public void SessionsListsEverySessionAndMarksTheOneBeingTalkedTo()
    {
        using var input = new System.IO.StringReader(
            "/sessions" + Environment.NewLine);
        var alpha = new ConsoleSessionFixture("alpha");
        var beta = new ConsoleSessionFixture("beta");
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, output, [alpha.Binding, beta.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        string text = output.ToString();
        Assert.Contains("session alpha (talking to this one)", text);
        Assert.Contains("session beta", text);
        Assert.Empty(alpha.Submitted);
    }

    /// <summary>
    /// With more than one session every line the console prints says which
    /// session it belongs to, so two worlds cannot be read as one.
    /// </summary>
    [Fact]
    public void EveryPrintedLineNamesItsSessionWhenThereAreSeveral()
    {
        using var input = new System.IO.StringReader(
            "/session nowhere" + Environment.NewLine);
        var alpha = new ConsoleSessionFixture("alpha");
        var beta = new ConsoleSessionFixture("beta");
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, output, [alpha.Binding, beta.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.Contains("[alpha] ", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OneSessionPrintsNoSessionName()
    {
        using var input = new System.IO.StringReader(
            "/quit" + Environment.NewLine);
        var alpha = new ConsoleSessionFixture("alpha");
        var output = new StringWriter();
        using var quit = new CancellationTokenSource();
        using var controller = new HeadlessConsoleController(
            input, output, [alpha.Binding], quit);

        Assert.True(WaitForEndOfInput(controller));
        controller.DrainDue();

        Assert.DoesNotContain("[alpha]", output.ToString(), StringComparison.Ordinal);
    }



    // The console's own wording is gone: it prints the chat feed's lines.
    // See HeadlessConsoleChatParityTests.

    // ── HeadlessConsoleRenderer: N5 dim-weight rules ─────────────────────

    [Fact]
    public void ChatAndInterfaceTextPrintAtDefaultWeightNeverDimmed()
    {
        var log = new ChatLog();
        using var feed = new RuntimeChatFeed(log, new ChatWindowState());
        var output = new StringWriter();
        using var renderer = new HeadlessConsoleRenderer(
            output, useColor: true, chat: feed);

        log.OnLocalSpeech(
            "Bob", "hi", 0x50000010u, isRanged: false,
            logTextType: (uint)RetailLogTextType.Speech);
        renderer.WriteInterfaceText("Unknown command: /x");

        string text = output.ToString();
        Assert.DoesNotContain("[2m", text);
        Assert.Contains("[say] Bob says, \"hi\"", text);
        Assert.Contains("Unknown command: /x", text);
    }

    [Fact]
    public void LifecycleCommandAndPortalLinesAreDimmedWhenColorIsEnabled()
    {
        var output = new StringWriter();
        var renderer = new HeadlessConsoleRenderer(output, useColor: true);

        renderer.OnLifecycle(new RuntimeLifecycleDelta(
            default, RuntimeLifecycleState.Starting, RuntimeLifecycleState.InWorld));
        renderer.OnCommand(new RuntimeCommandDelta(
            default, RuntimeCommandDomain.Chat, 0, RuntimeCommandStatus.Rejected, Text: "boom"));
        renderer.OnPortal(new RuntimePortalDelta(
            default,
            new RuntimePortalSnapshot(
                Generation: 1,
                RuntimePortalKind.Portal,
                Readiness: new RuntimeDestinationReadiness(
                    1, 0x12345678u, false, false, 0, true, true, true),
                Materialized: true,
                Completed: false,
                Cancelled: false,
                WorldViewportObserved: true,
                WorldSimulationAvailable: true,
                InvariantFailureCount: 0,
                WaitCueShown: false,
                PortalMaterializationCount: 1)));

        string[] lines = output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Contains("[2m", line));
        // And each carries the marker that tells a notice from a chat line.
        Assert.All(
            lines,
            line => Assert.Contains(
                HeadlessConsoleRenderer.NoticePrefix, line, StringComparison.Ordinal));
    }

    // ── HeadlessSessionHost.SubmitConsoleLine: the real dispatch pipeline ─

    [Fact]
    public void SlashSayProducesTheSameOutboundTalkActionTheGraphicalRouteSends()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        SubmitOutcome outcome = host.SubmitConsoleLine("/say hello");

        Assert.Equal(SubmitOutcome.Sent, outcome);
        byte[] body = Assert.Single(captured);
        Assert.Equal(ChatRequests.TalkOpcode, ActionOpcode(body));
        Assert.Equal("hello", TalkText(body));
    }

    [Fact]
    public void PlainTextProducesTheSameOutboundTalkActionAsSlashSay()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        SubmitOutcome outcome = host.SubmitConsoleLine("hello");

        Assert.Equal(SubmitOutcome.Sent, outcome);
        byte[] body = Assert.Single(captured);
        Assert.Equal(ChatRequests.TalkOpcode, ActionOpcode(body));
        Assert.Equal("hello", TalkText(body));
    }

    [Fact]
    public void PluginVerbReachesTheRegisteredPluginCommandWithoutTouchingTheWire()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);
        var received = new List<PluginCommand>();
        using IDisposable registration = host.PluginCommands.Register(
            "vt",
            command => received.Add(command));

        SubmitOutcome outcome = host.SubmitConsoleLine("/vt start");

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        PluginCommand command = Assert.Single(received);
        Assert.Equal("vt", command.Verb);
        Assert.Equal("start", command.Arguments);
        Assert.Empty(captured);
    }

    [Fact]
    public void UnknownVerbProducesTheSameChatLineTheChatBoxShows()
    {
        var operations = new FixtureSessionOperations();
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            host.Start().Status);

        SubmitOutcome outcome = host.SubmitConsoleLine("/");

        Assert.Equal(SubmitOutcome.UnknownCommand, outcome);
        var chatEntry = Assert.Single(host.Runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Contains("Unknown command:", chatEntry.Text);
        SpewBoxState spewBox = host.Runtime.CommunicationOwner.SpewBox;
        spewBox.Tick(host.Runtime.Clock.SimulationTimeSeconds);
        Assert.Empty(spewBox.Snapshot());
    }

    /// <summary>
    /// The console types into the one chat entry, so where a plain line goes
    /// is decided the same way it is in a chat box: by the channel the entry
    /// is aimed at. Before this the console always said Say.
    /// </summary>
    [Fact]
    public void APlainLineGoesToTheChannelTheChatEntryIsAimedAt()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);

        host.ChatEntry.SetChannel(ChatChannelKind.General);
        SubmitOutcome outcome = host.SubmitConsoleLine("hello");

        Assert.Equal(SubmitOutcome.Sent, outcome);
        Assert.DoesNotContain(
            captured, body => ActionOpcode(body) == ChatRequests.TalkOpcode);
    }

    /// <summary>
    /// A console user can reply to a tell exactly as a chat box user does:
    /// the entry is aimed at a listener and a plain line is a tell to them.
    /// </summary>
    [Fact]
    public void APlainLineGoesToTheListenerTheChatEntryIsAimedAt()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);

        host.ChatEntry.SetTellTarget("Bob", 0u);
        SubmitOutcome outcome = host.SubmitConsoleLine("meet me");

        Assert.Equal(SubmitOutcome.Sent, outcome);
        byte[] body = Assert.Single(captured);
        Assert.Equal(ChatRequests.TellOpcode, ActionOpcode(body));
    }

    /// <summary>
    /// A line typed at the console is remembered by the same history a chat
    /// box recalls from, so the two front ends share one list.
    /// </summary>
    [Fact]
    public void ALineTypedAtTheConsoleIsRememberedForRecall()
    {
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = static _ => { },
        };
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);

        _ = host.SubmitConsoleLine("hello");

        Assert.Equal("hello", host.ChatEntry.RecallPrevious());
    }

    /// <summary>
    /// A null line sends the draft as it stands, which is what Enter on an
    /// empty console line -- and on an empty chat entry -- does.
    /// </summary>
    [Fact]
    public void ANullLineSendsTheDraftAPluginStaged()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        using var credential = new HeadlessCredentialSecret("fixture", "password");
        using var host = new HeadlessSessionHost(
            Descriptor(),
            credential,
            new HeadlessDiagnosticWriter(TextWriter.Null),
            operations);
        Assert.Equal(RuntimeSessionStartStatus.Connected, host.Start().Status);

        Assert.True(host.ChatEntry.Compose("hello"));
        SubmitOutcome outcome = host.SubmitConsoleLine(null);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        byte[] body = Assert.Single(captured);
        Assert.Equal(ChatRequests.TalkOpcode, ActionOpcode(body));
        Assert.Equal("hello", TalkText(body));
        Assert.Equal(string.Empty, host.ChatEntry.Draft);
    }

    // ── HeadlessConsoleSpewBoxPump: server/plugin-driven interface text ──

    [Fact]
    public void PumpPrintsInterfaceTextNotOriginatingFromTheConsole()
    {
        var spewBox = new SpewBoxState();
        var printed = new List<string>();
        double now = 0d;
        var pump = new HeadlessConsoleSpewBoxPump(spewBox, () => now, printed.Add);

        spewBox.Enqueue("[vt] navigation route loaded");

        pump.Pump();

        Assert.Equal(["[vt] navigation route loaded"], printed);

        // A second pump with nothing new enqueued must not reprint the
        // still-visible entry.
        now += 0.1d;
        pump.Pump();
        Assert.Equal(["[vt] navigation route loaded"], printed);
    }


    [Fact]
    public async Task ConsoleLineReachesTheSessionAndQuitEndsTheProcessGracefully()
    {
        var captured = new List<byte[]>();
        var operations = new FixtureSessionOperations
        {
            GameActionCapture = body => captured.Add(body),
        };
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions = [Descriptor()],
        };
        using var diagnostics = new StringWriter();
        using var input = new System.IO.StringReader(
            "hello" + Environment.NewLine + "/quit" + Environment.NewLine);
        using var host = new HeadlessProcessHost(
            configuration,
            IsolatedHeadlessPaths.Create(),
            input,
            diagnostics,
            operations,
            new FakeTimeProvider(),
            directCredentials: new HeadlessDirectCredentials("account", "password"),
            consoleEnabled: true);

        HeadlessExitCode exitCode = await host.RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(HeadlessExitCode.Success, exitCode);
        byte[] body = Assert.Single(captured);
        Assert.Equal(ChatRequests.TalkOpcode, ActionOpcode(body));
        Assert.Equal("hello", TalkText(body));
    }

    [Fact]
    public async Task PluginRequestCloseEndsItsOwnSessionWhileASiblingSessionKeepsRunning()
    {
        // A plugin's Window.RequestClose ends only its own
        // session -- not the whole process -- so a second session hosted
        // by the same process is untouched. Only the console's /quit and
        // a SIGINT/SIGTERM cancel the process-wide token that stops every
        // session at once (see
        // ConsoleLineReachesTheSessionAndQuitEndsTheProcessGracefully
        // above for that path). consoleEnabled is false here on purpose:
        // this is the route available when there is no console to type
        // /quit into.
        string statusPathAlpha = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-requestclose-alpha-{Guid.NewGuid():N}.jsonl");
        string statusPathBeta = Path.Combine(
            Path.GetTempPath(),
            $"acdream-headless-requestclose-beta-{Guid.NewGuid():N}.jsonl");
        try
        {
            HeadlessSessionDescriptor StandardInputDescriptor(
                string id, string statusFile) =>
                Descriptor() with
                {
                    Id = id,
                    StatusFile = statusFile,
                    Credential = new HeadlessCredentialReference
                    {
                        Provider = HeadlessCredentialProviderKind.StandardInput,
                        Reference = "fixture",
                    },
                };
            var configuration = new HeadlessConfiguration
            {
                Version = 1,
                Sessions =
                [
                    StandardInputDescriptor("alpha", statusPathAlpha),
                    StandardInputDescriptor("beta", statusPathBeta),
                ],
            };
            var operations = new FixtureSessionOperations();
            using var diagnostics = new StringWriter();
            using var input = new System.IO.StringReader(
                "password-alpha" + Environment.NewLine
                + "password-beta" + Environment.NewLine);
            using var host = new HeadlessProcessHost(
                configuration,
                IsolatedHeadlessPaths.Create(),
                input,
                diagnostics,
                operations,
                new FakeTimeProvider(),
                directCredentials: null,
                consoleEnabled: false);

            HeadlessSessionHost alpha = host.Sessions[0];
            HeadlessSessionHost beta = host.Sessions[1];
            HostWindowResult? observed = null;
            alpha.Plugins.Host.Events.LoginComplete += () =>
            {
                observed = alpha.Plugins.Host.Window.RequestClose();
            };

            using var cts = new CancellationTokenSource();
            Task<HeadlessExitCode> run = host.RunAsync(cts.Token);

            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!File.Exists(statusPathAlpha)
                || !ReadWhileTheHostWrites(statusPathAlpha).Contains("\"exited\""))
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        "alpha never reached its own terminal event.");
                }
                await Task.Delay(10);

            }
            Assert.Equal(HostWindowStatus.Done, observed?.Status);
            Assert.True(alpha.IsPolicyComplete);

            // beta starts a moment after alpha (sessions start in order in
            // the same background thread); wait for its own "enteredWorld"
            // write rather than racing it.
            DateTime betaDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!File.Exists(statusPathBeta)
                || !ReadWhileTheHostWrites(statusPathBeta)
                    .Contains("\"enteredWorld\""))
            {
                if (DateTime.UtcNow > betaDeadline)
                    throw new TimeoutException("beta never reached enteredWorld.");
                await Task.Delay(10);
            }

            Assert.False(beta.IsPolicyComplete);
            Assert.DoesNotContain(
                "\"exited\"",
                ReadWhileTheHostWrites(statusPathBeta));

            cts.Cancel();
            HeadlessExitCode exitCode = await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HeadlessExitCode.Success, exitCode);

            JsonElement alphaExited = JsonDocument.Parse(
                ReadWhileTheHostWrites(statusPathAlpha)
                    .Split(
                        Environment.NewLine,
                        StringSplitOptions.RemoveEmptyEntries)
                    .Single(line => line.Contains("\"exited\"")))
                .RootElement.Clone();
            Assert.Equal(0, alphaExited.GetProperty("code").GetInt32());
            Assert.Equal("graceful", alphaExited.GetProperty("reason").GetString());
        }
        finally
        {
            if (File.Exists(statusPathAlpha))
                File.Delete(statusPathAlpha);
            if (File.Exists(statusPathBeta))
                File.Delete(statusPathBeta);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task StandardOutputIsTerminalParameterControlsColorNotTheRealConsole(
        bool standardOutputIsTerminal, bool expectDimmed)
    {
        var operations = new FixtureSessionOperations();
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions = [Descriptor()],
        };
        using var diagnostics = new StringWriter();
        using var input = new System.IO.StringReader("/quit" + Environment.NewLine);
        using var host = new HeadlessProcessHost(
            configuration,
            IsolatedHeadlessPaths.Create(),
            input,
            diagnostics,
            operations,
            new FakeTimeProvider(),
            directCredentials: new HeadlessDirectCredentials("account", "password"),
            consoleEnabled: true,
            standardOutputIsTerminal: standardOutputIsTerminal);

        await host.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        string text = diagnostics.ToString();
        Assert.Contains("entered world", text);
        Assert.Equal(expectDimmed, text.Contains("[2m"));
    }

    /// <summary>
    /// The console used to refuse to run beside more than one session and
    /// said "single-session only". It now serves every session in the
    /// process: each one gets its own reader of its own chat feed, and each
    /// can be typed at.
    /// </summary>
    [Fact]
    public void EverySessionInTheProcessGetsTheConsole()
    {
        HeadlessSessionDescriptor StandardInputDescriptor(string id) =>
            Descriptor() with
            {
                Id = id,
                Credential = new HeadlessCredentialReference
                {
                    Provider = HeadlessCredentialProviderKind.StandardInput,
                    Reference = "fixture",
                },
            };
        var configuration = new HeadlessConfiguration
        {
            Version = 1,
            Sessions =
            [
                StandardInputDescriptor("one"),
                StandardInputDescriptor("two"),
            ],
        };
        var operations = new FixtureSessionOperations();
        using var diagnostics = new StringWriter();
        using var input = new System.IO.StringReader(
            "password-one" + Environment.NewLine
            + "password-two" + Environment.NewLine);
        using var host = new HeadlessProcessHost(
            configuration,
            IsolatedHeadlessPaths.Create(),
            input,
            diagnostics,
            operations,
            new FakeTimeProvider(),
            directCredentials: null,
            consoleEnabled: true);

        Assert.DoesNotContain("single-session only", diagnostics.ToString());
        Assert.Equal(2, host.Sessions.Count);
        Assert.All(host.Sessions, session => Assert.NotNull(session.ConsolePump));
    }

    private static bool WaitForEndOfInput(HeadlessConsoleController controller) =>
        controller.Reader.EndOfInput.Wait(TimeSpan.FromSeconds(5));

    private static uint ActionOpcode(byte[] body) =>
        BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8, sizeof(uint)));

    private static string TalkText(byte[] body)
    {
        ushort length = BinaryPrimitives.ReadUInt16LittleEndian(
            body.AsSpan(12, sizeof(ushort)));
        return Encoding.ASCII.GetString(body, 14, length);
    }

    private static HeadlessSessionDescriptor Descriptor() => new()
    {
        Id = "console-bot",
        Endpoint = new HeadlessEndpointDescriptor
        {
            Host = "127.0.0.1",
            Port = 9000,
        },
        Account = "account",
        Character = new HeadlessCharacterSelector
        {
            Name = "headless",
        },
        Policy = new HeadlessBotPolicyDescriptor
        {
            Id = "idle",
        },
        Credential = new HeadlessCredentialReference
        {
            Provider = HeadlessCredentialProviderKind.Environment,
            Reference = "CONSOLE_BOT_PASSWORD",
        },
    };

    /// <summary>
    /// Reads a status file while the host is still appending to it.
    /// <see cref="File.ReadAllText(string)"/> cannot be used for that: it
    /// opens the file in a mode that locks a writer out, so a status line
    /// written while the read is in flight fails, and the writer answers a
    /// failed write by turning that session's whole status stream off for
    /// good. Watching a live session then silences the very thing being
    /// watched, and the watcher waits forever for a line that will never
    /// come. Sharing the file for writing as well leaves the host free to
    /// keep writing while this reads.
    /// </summary>
    private static string ReadWhileTheHostWrites(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        public Action<byte[]>? GameActionCapture { get; init; }

        public CharacterList.Parsed? Characters { get; init; } = new(
            0u,
            [
                new CharacterList.Character(0x50000001u, "Other", 0u),
                new CharacterList.Character(0x50000002u, "Headless", 0u),
            ],
            [],
            11,
            "account",
            true,
            true);

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            var session = new WorldSession(endpoint);
            session.GameActionCapture = GameActionCapture;
            return session;
        }

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed? GetCharacters(WorldSession session) =>
            Characters;

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class ThreadIdRecordingTextReader(TextReader inner)
        : TextReader
    {
        internal int? ReadLineThreadId { get; private set; }

        public override string? ReadLine()
        {
            ReadLineThreadId = Environment.CurrentManagedThreadId;
            return inner.ReadLine();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        public override long GetTimestamp() => Stopwatch.GetTimestamp();

        public override long TimestampFrequency => Stopwatch.Frequency;
    }
}
