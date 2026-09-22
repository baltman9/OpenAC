using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

/// <summary>
/// Several characters played side by side on one machine, told what to do
/// from one of them. A client asks the others to run a command line, and
/// each of them runs it on its own command bus -- the same bus a line the
/// player types arrives on -- so the answer to a broadcast cannot depend on
/// which plugins happen to be loaded.
///
/// Both clients have to do this or a plugin's behaviour changes depending on
/// whether there is a window. The writing side rides the tick the note is
/// already written on; the reading side is a file read either client does
/// for itself. These scenarios drive both through <see cref="IPluginHost"/>,
/// which is the only way a plugin ever reaches it.
///
/// A value is recorded AND asserted here. Recording alone only compares the
/// two clients, and two clients agreeing on a wrong number is still wrong.
///
/// Mutation checks (2026-09-22):
/// * leaving the command ring out of the note turned
///   <see cref="EitherClientTellsTheMachineWhatItAskedTheOthersToRun"/> red
///   with "told the machine nothing about the line it broadcast";
/// * never handing a taken line to the command bus turned
///   <see cref="EitherClientRunsALineAnotherClientOnTheMachineAsked"/> red
///   with "did not run the other client's line";
/// * ignoring this client's own labels when reading turned
///   <see cref="EitherClientLetsAPluginChangeTheLabelsItAnswersTo"/> red.
/// </summary>
public sealed class PeerCommandParityTests
{
    /// <summary>
    /// A second client on this machine, with a fixed identity so both arms
    /// read the same client id and the two transcripts can be compared.
    /// </summary>
    private static readonly Guid OtherClient =
        Guid.Parse("7ea50000-0000-0000-0000-0000000000c8");

    /// <summary>The character that second client is playing.</summary>
    private const uint OtherCharacter = 0x50000003u;

    /// <summary>The verb both arms answer, so a delivered line has somewhere to land.</summary>
    private const string Verb = "parityecho";

    /// <summary>
    /// The writing side: what a plugin broadcasts reaches the note another
    /// client on this machine reads, on either client.
    /// </summary>
    [Fact]
    public void EitherClientTellsTheMachineWhatItAskedTheOthersToRun() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);

            INetworkAutomation network = arm.Host.Automation.Network;
            transcript.Step("broadcast");
            transcript.Record(
                "accepted",
                network.BroadcastCommand($"/{Verb} go", ["squad"], 250));
            // The client writes its note on its own tick, as it does for
            // everything else it tells the machine about itself.
            arm.Advance();

            using var onlooker = new LocalPluginPeerRegistry(
                arm.PeerDirectory, timeProvider: null, OtherClient);
            LocalPluginPeerCommand[] seen = onlooker.CaptureRemoteCommands(
                0L,
                arm.Host.Automation.Character.WorldName,
                OtherCharacter,
                ["squad"])
                .ToArray();
            Assert.True(
                seen.Length == 1,
                $"{arm.Name} told the machine nothing about the line it "
                + $"broadcast: {seen.Length} lines in {arm.PeerDirectory}.");
            PluginPeerCommand only = seen[0].Command;
            transcript.Record("sequence", only.Sequence);
            // Which client instance asked, not WHICH NUMBER it drew: a
            // client identifies itself with a fresh id every run.
            transcript.Record("clientIdIsSet", only.ClientId != 0u);
            transcript.Record("senderObjectId", only.SenderObjectId);
            transcript.Record("line", only.Line);
            transcript.Record("tags", string.Join(",", only.Tags));
            // The onlooker is the only recipient, so it holds the first
            // place behind the sender and waits one delay.
            transcript.Record("stagger", seen[0].StaggerMilliseconds);
            Assert.Equal(
                arm.Host.Automation.Character.ObjectId, only.SenderObjectId);
            Assert.Equal($"/{Verb} go", only.Line);
            Assert.Equal(["squad"], only.Tags);
            Assert.Equal(250, seen[0].StaggerMilliseconds);
        });

    /// <summary>
    /// The reading side: a line another client on this machine asked for is
    /// run on this client's own command bus, on either client.
    /// </summary>
    [Fact]
    public void EitherClientRunsALineAnotherClientOnTheMachineAsked() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            var ran = new List<string>();
            using IDisposable verb = arm.Host.Commands.Register(
                Verb, command => ran.Add(command.RawText));

            using (var other = new LocalPluginPeerRegistry(
                arm.PeerDirectory, timeProvider: null, OtherClient))
            {
                Assert.True(other.RecordCommand(new LocalPluginCommand(
                    SenderObjectId: OtherCharacter,
                    Tags: [],
                    Line: $"/{Verb} go",
                    DelayMilliseconds: 0)));
                other.Publish(OtherClientNote(arm));

                transcript.Step("run");
                // The client reads the folder on its own tick and runs what
                // it was asked to; no plugin is involved in the delivery.
                arm.Advance();
                transcript.Record("linesRun", ran.Count);
                transcript.Record("line", ran.Count == 1 ? ran[0] : "");
                Assert.True(
                    ran.Count == 1,
                    $"{arm.Name} did not run the other client's line: "
                    + $"{ran.Count} lines from {arm.PeerDirectory}.");
                Assert.Equal($"/{Verb} go", ran[0]);

                // The same line is there for a plugin to read as well, with
                // a cursor of its own, and reading it changed nothing.
                PluginPeerCommand[] read = arm.Host.Automation.Network
                    .CaptureCommands(0L)
                    .ToArray();
                transcript.Record("readByAPlugin", read.Length);
                Assert.True(
                    read.Length == 1,
                    $"{arm.Name} did not hand the line to a plugin: "
                    + $"{read.Length} lines.");
                transcript.Record(
                    "afterTheCursor",
                    arm.Host.Automation.Network
                        .CaptureCommands(read[0].Sequence).Count);
                Assert.Empty(
                    arm.Host.Automation.Network
                        .CaptureCommands(read[0].Sequence));
                transcript.Record("stillRunOnce", ran.Count);
                Assert.Single(ran);
            }
        });

    /// <summary>
    /// A broadcast is not limited to plugin verbs. The line goes in where a
    /// line the player typed goes in, so one of the client's OWN commands
    /// and a plain line of speech both do what typing them here would do.
    /// Handing the line to the verb registry alone -- which is what the
    /// delivery used to do -- took both of these from the sender, staggered
    /// them and dropped them.
    /// </summary>
    [Fact]
    public void EitherClientRunsABroadcastThatNoPluginVerbClaims() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);

            using (var other = new LocalPluginPeerRegistry(
                arm.PeerDirectory, timeProvider: null, OtherClient))
            {
                // One of the client's own commands, which reaches the server
                // as a client action of its own, and one plain line, which
                // reaches it as speech.
                Assert.True(other.RecordCommand(new LocalPluginCommand(
                    SenderObjectId: OtherCharacter,
                    Tags: [],
                    Line: "@permit add Bob",
                    DelayMilliseconds: 0)));
                Assert.True(other.RecordCommand(new LocalPluginCommand(
                    OtherCharacter, [], "hello", 0)));
                other.Publish(OtherClientNote(arm));
                // What staging the world put on the wire is not this
                // scenario's; what follows is.
                _ = arm.Operations.TakeOutbound();

                transcript.Step("run");
                arm.Advance();
                ParityOutbound[] sent = [.. arm.Operations.Outbound];
                transcript.RecordOutbound(arm);

                ParityOutbound permit = Assert.Single(
                    sent, message => message.GameAction == PermitAction);
                Assert.Equal("Bob", TextAt(permit, 12));
                ParityOutbound spoke = Assert.Single(
                    sent, message => message.GameAction == TalkAction);
                Assert.Equal("hello", TextAt(spoke, 12));
            }
        });

    /// <summary>
    /// The client action that asks the server to let another player at this
    /// character's corpse, which is what <c>@permit add</c> becomes.
    /// </summary>
    private const uint PermitAction = 0x0219u;

    /// <summary>The client action a line of speech becomes.</summary>
    private const uint TalkAction = 0x0015u;

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
    /// The labels: a plugin can change the words this client answers to, the
    /// change reaches the note the other clients read, and a line aimed at
    /// the old word is no longer run here.
    /// </summary>
    [Fact]
    public void EitherClientLetsAPluginChangeTheLabelsItAnswersTo() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            var ran = new List<string>();
            using IDisposable verb = arm.Host.Commands.Register(
                Verb, command => ran.Add(command.RawText));

            transcript.Step("relabelled");
            transcript.Record(
                "accepted", arm.Host.Automation.Network.SetTags(["healer"]));
            arm.Advance();

            using var onlooker = new LocalPluginPeerRegistry(
                arm.PeerDirectory, timeProvider: null, OtherClient);
            PluginNetworkClient seen = Assert.Single(
                onlooker.CaptureRemoteClients());
            transcript.Record("tags", string.Join(",", seen.Tags));
            Assert.Equal(["healer"], seen.Tags);

            using (var other = new LocalPluginPeerRegistry(
                arm.PeerDirectory, timeProvider: null, OtherClient))
            {
                // One line for a label this client no longer wears, one for
                // the label it just took.
                Assert.True(other.RecordCommand(new LocalPluginCommand(
                    OtherCharacter,
                    [ParityArm.ConfiguredPluginTags[0]],
                    $"/{Verb} old",
                    0)));
                Assert.True(other.RecordCommand(new LocalPluginCommand(
                    OtherCharacter, ["healer"], $"/{Verb} new", 0)));
                other.Publish(OtherClientNote(arm));

                transcript.Step("run");
                AdvanceUntilTheFolderIsReadAgain(arm);
                transcript.Record("linesRun", ran.Count);
                transcript.Record("line", ran.Count == 1 ? ran[0] : "");
                Assert.True(
                    ran.Count == 1,
                    $"{arm.Name} ran {ran.Count} lines rather than the one "
                    + "aimed at the label it answers to: "
                    + string.Join(" | ", ran));
                Assert.Equal($"/{Verb} new", ran[0]);
            }
        });

    /// <summary>
    /// Runs the client until it looks at the other clients' notes again. A
    /// client reads the folder on a period of its own rather than on every
    /// frame, so a scenario that has already had one read has to let that
    /// period go by before the next note is seen.
    /// </summary>
    private static void AdvanceUntilTheFolderIsReadAgain(ParityArm arm)
    {
        int ticks = (int)Math.Ceiling(
            LocalPluginPeerRegistry.CommandPollPeriod.TotalSeconds
                / ParityArm.TickSeconds) + 1;
        for (int tick = 0; tick < ticks; tick++)
            arm.Advance();
    }

    /// <summary>
    /// What the second client says about itself. Its world has to be this
    /// client's world -- a client logged in somewhere else names other
    /// creatures entirely -- and the arms have no world name, so it copies
    /// whatever this one reports.
    /// </summary>
    private static PluginNetworkClient OtherClientNote(ParityArm arm) => new(
        0u,
        OtherCharacter,
        "Onlooker",
        arm.Host.Automation.Character.WorldName,
        new PluginNavigationPosition(
            ParityPlayerBody.Cell,
            ParityWorld.PlayerX,
            ParityWorld.PlayerY,
            ParityPlayerBody.GroundHeight,
            0f,
            true),
        [],
        100u, 100u, 100u, 100u, 100u, 100u,
        0f);
}
