using AcDream.Core.Chat;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Chat;

namespace AcDream.Runtime.Tests.Chat;

/// <summary>
/// Plugin input interceptors at the one place every typed line passes: the
/// command router. Where they sit in the order is what these pin -- after the
/// client's own command catalogue, before plugin verbs and before a line is
/// aimed at a channel -- along with the rewrite loop bound and that a
/// suppressed line leaves no trace.
/// </summary>
public sealed class ChatCommandRouterInterceptorTests
{
    /// <summary>
    /// A bus that keeps what was published, answers plugin verbs from a list
    /// and runs interceptors the way a host's bus does: the first one that
    /// does not pass decides.
    /// </summary>
    private sealed class InterceptingBus : IPluginCommandBus
    {
        internal List<object> Published { get; } = [];
        internal List<string> Seen { get; } = [];
        internal List<string> VerbsOffered { get; } = [];
        internal HashSet<string> PluginVerbs { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<Func<string, PluginChatInputDecision>> Interceptors { get; } = [];
        internal Action<string>? OnPluginVerb { get; set; }

        public void Publish<T>(T command) where T : notnull =>
            Published.Add(command);

        public bool TryHandlePluginCommand(string commandLine)
        {
            VerbsOffered.Add(commandLine);
            string verb = commandLine.Split(' ')[0].TrimStart('/', '@');
            if (!PluginVerbs.Contains(verb))
                return false;
            OnPluginVerb?.Invoke(commandLine);
            return true;
        }

        public PluginChatInputDecision InterceptChatInput(string typed)
        {
            Seen.Add(typed);
            foreach (Func<string, PluginChatInputDecision> interceptor in Interceptors)
            {
                PluginChatInputDecision decision = interceptor(typed);
                if (decision.Action != PluginChatInputAction.Pass)
                    return decision;
            }
            return PluginChatInputDecision.Pass;
        }
    }

    private sealed class RecordingFeedback : IChatCommandFeedback
    {
        internal List<string> Lines { get; } = [];

        public string? LastIncomingTellSender { get; set; }

        public string? LastOutgoingTellTarget { get; set; }

        public void ShowInterfaceText(string text) => Lines.Add(text);

        public void ShowSystemMessage(string text) => Lines.Add(text);
    }

    private static SubmitOutcome Submit(
        string line, InterceptingBus bus, RecordingFeedback? feedback = null) =>
        ChatCommandRouter.Submit(
            line, feedback ?? new RecordingFeedback(), bus, ChatChannelKind.Say);

    [Fact]
    public void ARewrittenLineIsWhatIsSaid()
    {
        var bus = new InterceptingBus();
        bus.Interceptors.Add(static typed => typed.Contains("[loc]")
            ? PluginChatInputDecision.Rewrite(typed.Replace("[loc]", "12.3N, 45.6E"))
            : PluginChatInputDecision.Pass);

        SubmitOutcome outcome = Submit("I am at [loc]", bus);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        var said = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal("I am at 12.3N, 45.6E", said.Text);
        Assert.Equal(ChatChannelKind.Say, said.Channel);
    }

    [Fact]
    public void ARewrittenTellKeepsItsListener()
    {
        var bus = new InterceptingBus();
        bus.Interceptors.Add(static typed =>
            PluginChatInputDecision.Rewrite(typed.Replace("[loc]", "12.3N, 45.6E")));

        SubmitOutcome outcome = Submit("/tell Bob, meet me at [loc]", bus);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        var told = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal(ChatChannelKind.Tell, told.Channel);
        Assert.Equal("Bob", told.TargetName);
        Assert.Equal("meet me at 12.3N, 45.6E", told.Text);
    }

    [Fact]
    public void ASuppressedLineGoesNowhereAndSaysNothing()
    {
        var bus = new InterceptingBus();
        bus.Interceptors.Add(static _ => PluginChatInputDecision.Suppress);
        var feedback = new RecordingFeedback();

        SubmitOutcome outcome = Submit("hello", bus, feedback);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
        Assert.Empty(bus.VerbsOffered);
        Assert.Empty(feedback.Lines);
    }

    [Fact]
    public void ARewriteToNothingIsASuppression()
    {
        var bus = new InterceptingBus();
        bus.Interceptors.Add(static _ => PluginChatInputDecision.Rewrite("   "));

        SubmitOutcome outcome = Submit("hello", bus);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Published);
    }

    /// <summary>
    /// The client's own commands are consulted first: an interceptor that
    /// would swallow everything never sees one, so no plugin can shadow or
    /// rewrite a command the client claims.
    /// </summary>
    [Theory]
    [InlineData("/version")]
    [InlineData("@loc")]
    [InlineData("/help")]
    public void AClientCommandNeverReachesAnInterceptor(string line)
    {
        var bus = new InterceptingBus();
        bus.Interceptors.Add(static _ => PluginChatInputDecision.Suppress);
        var feedback = new RecordingFeedback();

        SubmitOutcome outcome = Submit(line, bus, feedback);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Empty(bus.Seen);
        // The command really ran: it was published to the client or it
        // answered the player itself, so this is not two ways of doing
        // nothing agreeing with each other.
        Assert.True(
            bus.Published.OfType<ExecuteClientCommandCmd>().Any()
                || feedback.Lines.Count > 0,
            $"{line} was neither run nor answered.");
    }

    /// <summary>
    /// Interceptors come before plugin verbs: a rewrite can turn a plain
    /// alias into a verb some plugin registered, and the verb then runs.
    /// </summary>
    [Fact]
    public void ARewriteCanBecomeAPluginVerb()
    {
        var bus = new InterceptingBus();
        bus.PluginVerbs.Add("vt");
        bus.Interceptors.Add(static typed => typed == "go"
            ? PluginChatInputDecision.Rewrite("/vt start")
            : PluginChatInputDecision.Pass);

        SubmitOutcome outcome = Submit("go", bus);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Equal(["/vt start"], bus.VerbsOffered);
        Assert.Equal(["go", "/vt start"], bus.Seen);
        Assert.Empty(bus.Published);
    }

    /// <summary>
    /// A plugin verb typed directly still passes the interceptors first, so
    /// an alias can rewrite a verb's arguments before the verb sees them.
    /// </summary>
    [Fact]
    public void APluginVerbIsInterceptedBeforeItRuns()
    {
        var bus = new InterceptingBus();
        bus.PluginVerbs.Add("vt");
        bus.Interceptors.Add(static typed => typed == "/vt here"
            ? PluginChatInputDecision.Rewrite("/vt 12.3N 45.6E")
            : PluginChatInputDecision.Pass);

        Submit("/vt here", bus);

        Assert.Equal(["/vt 12.3N 45.6E"], bus.VerbsOffered);
    }

    /// <summary>
    /// A rewrite that always produces something new would loop for ever. The
    /// router stops asking after a fixed number of passes and sends the last
    /// text as it stands.
    /// </summary>
    [Fact]
    public void ARewriteLoopIsBoundedAndTheLastTextIsSent()
    {
        var bus = new InterceptingBus();
        bus.Interceptors.Add(static typed => PluginChatInputDecision.Rewrite(typed + "!"));

        SubmitOutcome outcome = Submit("hello", bus);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        Assert.Equal(ChatCommandRouter.MaximumRewritePasses, bus.Seen.Count);
        var said = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal(
            "hello" + new string('!', ChatCommandRouter.MaximumRewritePasses),
            said.Text);
    }

    /// <summary>
    /// An interceptor that submits chat re-enters the router with a fresh
    /// rewrite count, so the rewrite bound does not cover it. The router
    /// counts how deep it is instead and refuses past a small bound with a
    /// notice, rather than recursing until the stack runs out.
    /// </summary>
    [Fact]
    public void AnInterceptorThatSubmitsChatIsRefusedPastTheReentrancyBound()
    {
        var bus = new InterceptingBus();
        var feedback = new RecordingFeedback();
        int deepest = 0;
        int depth = 0;
        bus.Interceptors.Add(typed =>
        {
            // The interceptor gives up on its own far past the router's
            // bound, so a router that does not refuse still ends, and is
            // seen to have let the recursion run that deep.
            if (++depth > 64)
                return PluginChatInputDecision.Pass;
            deepest = Math.Max(deepest, depth);
            try
            {
                Submit("again " + typed, bus, feedback);
            }
            finally
            {
                depth--;
            }
            return PluginChatInputDecision.Pass;
        });

        SubmitOutcome outcome = Submit("hello", bus, feedback);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        Assert.Equal(ChatCommandRouter.MaximumReentrancyDepth, deepest);
        Assert.Contains(feedback.Lines, line => line.Contains("dropped", StringComparison.Ordinal));
        // The submissions inside the bound went out; the one past it did not.
        Assert.Equal(ChatCommandRouter.MaximumReentrancyDepth, bus.Published.Count);
    }

    /// <summary>The same guard covers a plugin verb handler that submits chat.</summary>
    [Fact]
    public void AVerbHandlerThatSubmitsChatIsRefusedPastTheReentrancyBound()
    {
        var bus = new InterceptingBus();
        var feedback = new RecordingFeedback();
        int deepest = 0;
        int depth = 0;
        bus.PluginVerbs.Add("echo");
        bus.OnPluginVerb = line =>
        {
            if (++depth > 64)
                return;
            deepest = Math.Max(deepest, depth);
            try
            {
                Submit(line, bus, feedback);
            }
            finally
            {
                depth--;
            }
        };

        SubmitOutcome outcome = Submit("/echo hello", bus, feedback);

        Assert.Equal(SubmitOutcome.ClientHandled, outcome);
        Assert.Equal(ChatCommandRouter.MaximumReentrancyDepth, deepest);
        Assert.Contains(feedback.Lines, line => line.Contains("dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void ALineEveryInterceptorPassesGoesOutUnchanged()
    {
        var bus = new InterceptingBus();
        bus.Interceptors.Add(static _ => PluginChatInputDecision.Pass);
        bus.Interceptors.Add(static _ => PluginChatInputDecision.Pass);

        SubmitOutcome outcome = Submit("hello", bus);

        Assert.Equal(SubmitOutcome.Sent, outcome);
        Assert.Equal(["hello"], bus.Seen);
        var said = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal("hello", said.Text);
    }

    /// <summary>
    /// The interceptor sees the line trimmed and otherwise as typed; the
    /// leading-colon emote shorthand is the client's own and is expanded
    /// before the catalogue, so an interceptor never sees it.
    /// </summary>
    [Fact]
    public void TheInterceptorSeesTheTrimmedLineAsTyped()
    {
        var bus = new InterceptingBus();
        bus.Interceptors.Add(static _ => PluginChatInputDecision.Pass);

        Submit("   /tell Bob, hi   ", bus);
        Submit(":waves", bus);

        Assert.Equal(["/tell Bob, hi"], bus.Seen);
    }
}
