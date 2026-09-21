using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Chat;

namespace AcDream.HostParity.Tests;

/// <summary>
/// A plugin intercepting what the player types, on both clients, through each
/// client's own bus: the one a chat box, a console and a plugin's Submit all
/// hand a line to.
///
/// Every scenario asserts per arm on the words that reached the wire, or on
/// there being none, rather than only on the two clients agreeing: two buses
/// that both ignored the plugin would agree on sending the typed line as it
/// was, and prove nothing about interception.
///
/// Mutation check (2026-09-22), run: giving each arm's bus no interceptor
/// seam -- which is what both were before -- turned the rewrite, suppress and
/// loop-bound scenarios red on both arms at once, on the words that reached
/// the world, and left the client-command scenario green, which is why that
/// one also asserts the command really ran.
/// </summary>
public sealed class ChatInputInterceptorParityTests
{
    /// <summary>
    /// A marker inside a tell is replaced before the tell is sent. What the
    /// listener gets is the rewritten line, aimed at the same listener.
    /// </summary>
    [Fact]
    public void ARewrittenTellReachesTheWireRewrittenOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            using IDisposable alias = arm.Host.Automation.Chat.RegisterInputInterceptor(
                static typed => typed.Contains("[loc]", StringComparison.Ordinal)
                    ? PluginChatInputDecision.Rewrite(
                        typed.Replace("[loc]", "12.3N, 45.6E", StringComparison.Ordinal))
                    : PluginChatInputDecision.Pass);

            transcript.Step("tell with a marker");
            IReadOnlyList<ParityOutbound> sent =
                Type(arm, transcript, "/tell Bob, meet me at [loc]");
            // Said outright: the words that left the client are the
            // rewritten ones, and the marker is gone.
            Assert.Equal("meet me at 12.3N, 45.6E", TheToldLine(sent));

            transcript.Step("the plugin lets go");
            alias.Dispose();
            _ = arm.Operations.TakeOutbound();
            IReadOnlyList<ParityOutbound> after =
                Type(arm, transcript, "/tell Bob, back at [loc]");
            // With the interceptor gone the marker goes out as typed.
            Assert.Equal("back at [loc]", TheToldLine(after));
        });

    /// <summary>
    /// A suppressed line is sent nowhere and the player is told nothing:
    /// no message left the client, nothing was written into the feed, and
    /// nothing was queued for the player to read.
    /// </summary>
    [Fact]
    public void ASuppressedLineLeavesNoTraceOnEitherClient() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            using IDisposable swallow = arm.Host.Automation.Chat.RegisterInputInterceptor(
                static typed => typed.StartsWith("!", StringComparison.Ordinal)
                    ? PluginChatInputDecision.Suppress
                    : PluginChatInputDecision.Pass);
            int feedBefore = arm.Runtime.CommunicationOwner.ChatFeed.Snapshot().Count;

            transcript.Step("a swallowed line");
            SubmitOutcome outcome = Submit(arm, transcript, "!secret");
            IReadOnlyList<ParityOutbound> sent = [.. arm.Operations.Outbound];
            transcript.RecordOutbound(arm);
            transcript.Record(
                "feed.grew",
                arm.Runtime.CommunicationOwner.ChatFeed.Snapshot().Count - feedBefore);
            transcript.Record(
                "notices",
                arm.Runtime.CommunicationOwner.SpewBox.Snapshot().Length);
            Assert.Equal(SubmitOutcome.ClientHandled, outcome);
            Assert.Empty(sent);
            Assert.Equal(
                feedBefore,
                arm.Runtime.CommunicationOwner.ChatFeed.Snapshot().Count);
            Assert.Empty(arm.Runtime.CommunicationOwner.SpewBox.Snapshot());

            transcript.Step("a line the plugin passes");
            IReadOnlyList<ParityOutbound> spoken = Type(arm, transcript, "hello");
            // The interceptor is selective, not a dead bus: the next line
            // really goes out.
            Assert.Equal("hello", TheSpokenLine(spoken));
        });

    /// <summary>
    /// The client's own commands are consulted before any plugin: an
    /// interceptor that would swallow everything never sees one, and the
    /// command runs as if no plugin were loaded.
    /// </summary>
    [Fact]
    public void AClientCommandIsNeverOfferedToAnInterceptorOnEitherClient() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            int offered = 0;
            using IDisposable swallow = arm.Host.Automation.Chat.RegisterInputInterceptor(
                _ =>
                {
                    offered++;
                    return PluginChatInputDecision.Suppress;
                });
            int feedBefore = arm.Runtime.CommunicationOwner.ChatFeed.Snapshot().Count;

            transcript.Step("a client command");
            SubmitOutcome outcome = Submit(arm, transcript, "/version");
            transcript.Record("offered", offered);
            int feedGrew =
                arm.Runtime.CommunicationOwner.ChatFeed.Snapshot().Count - feedBefore;
            transcript.Record("feed.grew", feedGrew);
            Assert.Equal(SubmitOutcome.ClientHandled, outcome);
            Assert.Equal(0, offered);
            // The command really ran and answered the player, so this is not
            // an interceptor and a dead command agreeing on silence.
            Assert.True(feedGrew > 0, $"{arm.Name} did not run the command.");

            transcript.Step("plain speech is still intercepted");
            SubmitOutcome swallowed = Submit(arm, transcript, "hello");
            transcript.Record("offered", offered);
            Assert.Equal(SubmitOutcome.ClientHandled, swallowed);
            Assert.Equal(1, offered);
            Assert.Empty(arm.Operations.Outbound);
        });

    /// <summary>
    /// A rewrite that always produces something new is stopped after a fixed
    /// number of passes, and the last text is what reaches the wire.
    /// </summary>
    [Fact]
    public void ARewriteLoopIsBoundedTheSameOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            using IDisposable grow = arm.Host.Automation.Chat.RegisterInputInterceptor(
                static typed => PluginChatInputDecision.Rewrite(typed + "!"));

            transcript.Step("a line that never stops growing");
            IReadOnlyList<ParityOutbound> sent = Type(arm, transcript, "hello");
            Assert.Equal(
                "hello" + new string('!', ChatCommandRouter.MaximumRewritePasses),
                TheSpokenLine(sent));
        });

    /// <summary>
    /// A plugin whose interceptor throws does not take chat down with it:
    /// the line goes out, and the fault is not put to the player.
    /// </summary>
    [Fact]
    public void AThrowingInterceptorDoesNotBreakChatOnEitherClient() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            using IDisposable broken = arm.Host.Automation.Chat.RegisterInputInterceptor(
                static _ => throw new InvalidOperationException("plugin bug"));

            transcript.Step("say hello past a broken interceptor");
            IReadOnlyList<ParityOutbound> sent = Type(arm, transcript, "hello");
            Assert.Equal("hello", TheSpokenLine(sent));
            Assert.Empty(arm.Runtime.CommunicationOwner.SpewBox.Snapshot());
        });

    /// <summary>The client actions a typed line can leave as.</summary>
    private const uint TalkAction = 0x0015u;
    private const uint TellAction = 0x005Du;

    private static RuntimeChatEntryOwner Entry(ParityArm arm) =>
        arm.Runtime.CommunicationOwner.ChatEntryOwner;

    private static string TheSpokenLine(IReadOnlyList<ParityOutbound> sent) =>
        TextAt(OneMessage(sent, TalkAction), 12);

    private static string TheToldLine(IReadOnlyList<ParityOutbound> sent) =>
        TextAt(OneMessage(sent, TellAction), 12);

    private static ParityOutbound OneMessage(
        IReadOnlyList<ParityOutbound> sent, uint action) =>
        Assert.Single(sent, message => message.GameAction == action);

    /// <summary>
    /// One length-prefixed line of text out of a client action's body: two
    /// bytes of length, then the characters.
    /// </summary>
    private static string TextAt(ParityOutbound message, int offset)
    {
        byte[] body = Convert.FromHexString(message.Body);
        int length = System.Buffers.Binary.BinaryPrimitives
            .ReadUInt16LittleEndian(body.AsSpan(offset));
        return System.Text.Encoding.Latin1.GetString(
            body, offset + 2, length);
    }

    /// <summary>
    /// Types one line the way a person does -- into the one chat entry, onto
    /// this client's own bus -- and hands back what the route asked to send.
    /// </summary>
    private static IReadOnlyList<ParityOutbound> Type(
        ParityArm arm, ParityTranscript transcript, string line)
    {
        Submit(arm, transcript, line);
        ParityOutbound[] sent = [.. arm.Operations.Outbound];
        transcript.RecordOutbound(arm);
        return sent;
    }

    private static SubmitOutcome Submit(
        ParityArm arm, ParityTranscript transcript, string line)
    {
        SubmitOutcome outcome = Entry(arm).Submit(
            line,
            new RuntimeChatCommandFeedback(arm.Runtime.CommunicationOwner),
            arm.Commands);
        transcript.Record("outcome", outcome);
        return outcome;
    }
}
