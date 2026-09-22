using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// Asking the other clients on this computer to run a command line, and
/// running the lines they ask for. It is the one free-form channel between
/// clients, and the client itself runs what it is sent: a line reaches the
/// same command bus a typed line reaches, so which plugins happen to be
/// installed cannot change whether a broadcast is answered.
///
/// Everything read is checked rather than trusted: the note is a file
/// another process wrote, and a line out of it is handed to a command bus.
/// </summary>
public sealed class RuntimeAutomationSurfacePeerCommandTests
{
    private const uint LocalPlayer = 0x5000000Au;
    private const uint Peer = 0x5000000Bu;

    /// <summary>
    /// The reading side, and the whole point of the channel: a line another
    /// client asked for is run here, through the command bus a typed line
    /// goes through.
    ///
    /// Mutation check (2026-09-22): leaving the taken line in the queue --
    /// never handing it to the bus -- turned this red with no lines run.
    /// </summary>
    [Fact]
    public void ALineAnotherClientAskedForIsRunOnThisClientsCommandBus()
    {
        string root = TemporaryRoot();
        var time = new ManualTime(Noon);
        try
        {
            using GameRuntime runtime = Bound(
                root, out var surface, out _, out var events, time: time);
            using (surface)
            {
                List<string> ran = Verb(surface);
                using var peer = new LocalPluginPeerRegistry(root, time, Instance(1));
                Assert.True(peer.RecordCommand(
                    new LocalPluginCommand(Peer, [], "/example go", 0)));
                peer.Publish(Note(peer.ClientId, Peer, "Horan", []));

                events.FireTick(TickSeconds);

                Assert.Equal(["/example go"], ran);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A client runs a line once, however many times the sender's heartbeat
    /// republishes the same ring.
    ///
    /// Mutation check (2026-09-22): reading from zero on every poll instead
    /// of from this client's own cursor ran the line again on the second
    /// poll.
    /// </summary>
    [Fact]
    public void ALineIsRunOnceHoweverOftenTheSendersNoteIsRewritten()
    {
        string root = TemporaryRoot();
        var time = new ManualTime(Noon);
        try
        {
            using GameRuntime runtime = Bound(
                root, out var surface, out _, out var events, time: time);
            using (surface)
            {
                List<string> ran = Verb(surface);
                using var peer = new LocalPluginPeerRegistry(root, time, Instance(1));
                Assert.True(peer.RecordCommand(
                    new LocalPluginCommand(Peer, [], "/example go", 0)));
                peer.Publish(Note(peer.ClientId, Peer, "Horan", []));

                events.FireTick(TickSeconds);
                // The sender's heartbeat, carrying the same ring again.
                peer.Publish(Note(peer.ClientId, Peer, "Horan", []));
                Poll(events);

                Assert.Equal(["/example go"], ran);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// Labels pick the audience. A line aimed at labels this client does not
    /// answer to is not run here, and setting the labels through the plugin
    /// surface is what changes that.
    ///
    /// Mutation check (2026-09-22): reading with an empty label list instead
    /// of this client's own -- which makes every aimed line match nothing --
    /// turned the second half red.
    /// </summary>
    [Fact]
    public void ALineAimedAtLabelsIsRunOnlyByAClientWearingOne()
    {
        string root = TemporaryRoot();
        var time = new ManualTime(Noon);
        try
        {
            using GameRuntime runtime = Bound(
                root, out var surface, out _, out var events, time: time);
            using (surface)
            {
                List<string> ran = Verb(surface);
                using var peer = new LocalPluginPeerRegistry(root, time, Instance(1));
                Assert.True(peer.RecordCommand(
                    new LocalPluginCommand(Peer, ["healer"], "/example heal", 0)));
                peer.Publish(Note(peer.ClientId, Peer, "Horan", []));

                events.FireTick(TickSeconds);
                Assert.Empty(ran);

                Assert.True(surface.Network.SetTags(["Healer"]));
                Assert.True(peer.RecordCommand(
                    new LocalPluginCommand(Peer, ["healer"], "/example again", 0)));
                peer.Publish(Note(peer.ClientId, Peer, "Horan", []));
                Poll(events);

                Assert.Equal(["/example again"], ran);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A client does not run its own broadcast: it already knows what it
    /// asked for, and a plugin that wants the line run here runs it here.
    /// The note is in the folder, so this is the client refusing its own
    /// file rather than there being nothing to read.
    ///
    /// Mutation check (2026-09-22): two rules hold this, and dropping either
    /// one alone leaves it green. Dropping both -- the skip of this client's
    /// own file and the refusal of a line put under this character's name --
    /// turned it red with the line run here as well.
    /// </summary>
    [Fact]
    public void AClientDoesNotRunItsOwnBroadcast()
    {
        string root = TemporaryRoot();
        var time = new ManualTime(Noon);
        try
        {
            using GameRuntime runtime = Bound(
                root, out var surface, out var peers, out var events, time: time);
            using (surface)
            {
                List<string> ran = Verb(surface);
                Assert.True(surface.Network.BroadcastCommand(
                    "/example go", [], 0));
                peers.Publish(Note(peers.ClientId, LocalPlayer, "Acdream", []));

                // It really is in the note another client reads.
                using var onlooker = new LocalPluginPeerRegistry(
                    root, time, Instance(9));
                LocalPluginPeerCommand only = Assert.Single(
                    onlooker.CaptureRemoteCommands(0L, string.Empty, Peer, []));
                Assert.Equal("/example go", only.Command.Line);
                Assert.Equal(LocalPlayer, only.Command.SenderObjectId);

                // And with that same note in the folder, this client reads
                // the folder and runs nothing: the note is its own.
                events.FireTick(TickSeconds);

                Assert.Empty(ran);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// The stagger, as this client experiences it: with one other recipient
    /// ahead of it by client id, it waits two delays before running the
    /// line, and it waits on the same clock the line was stamped by.
    ///
    /// Mutation check (2026-09-22): running a taken line straight away,
    /// ignoring the wait, turned the first two assertions red.
    /// </summary>
    [Fact]
    public void ALineWaitsThisClientsPlaceInTheOrderBeforeItIsRun()
    {
        string root = TemporaryRoot();
        var time = new ManualTime(Noon);
        try
        {
            // This client is number three, so with the sender first and one
            // other recipient ahead of it, it holds the second place and
            // waits twice the delay.
            using GameRuntime runtime = Bound(
                root, out var surface, out _, out var events,
                time: time, instanceId: Instance(3));
            using (surface)
            {
                List<string> ran = Verb(surface);
                using var ahead = new LocalPluginPeerRegistry(
                    root, time, Instance(2));
                ahead.Publish(Note(ahead.ClientId, 0x5000000Cu, "Bystander", []));
                using var peer = new LocalPluginPeerRegistry(
                    root, time, Instance(1));
                Assert.True(peer.RecordCommand(
                    new LocalPluginCommand(Peer, [], "/example go", 100)));
                peer.Publish(Note(peer.ClientId, Peer, "Horan", []));

                events.FireTick(TickSeconds);
                Assert.Empty(ran);

                time.Advance(TimeSpan.FromMilliseconds(199));
                events.FireTick(TickSeconds);
                Assert.Empty(ran);

                time.Advance(TimeSpan.FromMilliseconds(2));
                events.FireTick(TickSeconds);
                Assert.Equal(["/example go"], ran);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A plugin can read the same lines the client is running, with a cursor
    /// of its own, and reading them does not stop the client running them.
    /// </summary>
    [Fact]
    public void APluginReadsTheSameLinesWithACursorOfItsOwn()
    {
        string root = TemporaryRoot();
        var time = new ManualTime(Noon);
        try
        {
            using GameRuntime runtime = Bound(
                root, out var surface, out _, out var events, time: time);
            using (surface)
            {
                List<string> ran = Verb(surface);
                using var peer = new LocalPluginPeerRegistry(root, time, Instance(1));
                Assert.True(peer.RecordCommand(
                    new LocalPluginCommand(Peer, [], "/example go", 0)));
                peer.Publish(Note(peer.ClientId, Peer, "Horan", []));

                PluginPeerCommand only = Assert.Single(
                    surface.Network.CaptureCommands(0L));
                Assert.Equal("/example go", only.Line);
                Assert.Equal(Peer, only.SenderObjectId);
                Assert.Equal(peer.ClientId, only.ClientId);
                Assert.Empty(only.Tags);
                Assert.Empty(surface.Network.CaptureCommands(only.Sequence));

                events.FireTick(TickSeconds);
                Assert.Equal(["/example go"], ran);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// What this client refuses to say. Each row is a line that would be
    /// wrong to pass on, and the refusal happens before anything is written.
    ///
    /// Mutation check (2026-09-22): removing the ring's rule from the
    /// broadcast turned the empty, control-character and delay rows red.
    /// </summary>
    [Theory]
    [InlineData("/example go", 0, true)]
    [InlineData("", 0, false)]
    [InlineData("   ", 0, false)]
    [InlineData("/example\tgo", 0, false)]
    [InlineData("/example go", -1, false)]
    [InlineData("/example go", 60_001, false)]
    public void ABroadcastThatMakesNoSenseIsNeverPassedOn(
        string line,
        int delayMilliseconds,
        bool accepted)
    {
        string root = TemporaryRoot();
        try
        {
            using GameRuntime runtime = Bound(
                root, out var surface, out var peers, out _);
            using (surface)
            {
                Assert.Equal(
                    accepted,
                    surface.Network.BroadcastCommand(line, [], delayMilliseconds));
                peers.Publish(Note(peers.ClientId, LocalPlayer, "Acdream", []));

                using var onlooker = new LocalPluginPeerRegistry(root);
                Assert.Equal(
                    accepted ? 1 : 0,
                    onlooker.CaptureRemoteCommands(
                        0L, string.Empty, Peer, []).Count);
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// A character that is not in the world has no name to put to a
    /// broadcast, so it asks for nothing rather than asking as nobody.
    /// </summary>
    [Fact]
    public void AClientWithNoCharacterBroadcastsNothing()
    {
        string root = TemporaryRoot();
        try
        {
            using GameRuntime runtime = Bound(
                root, out var surface, out _, out _, playerObjectId: 0u);
            using (surface)
            {
                Assert.False(surface.Network.BroadcastCommand(
                    "/example go", [], 0));
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>
    /// Setting the labels: a list is taken, a null one and a label too long
    /// to travel are refused rather than quietly cut short.
    /// </summary>
    [Fact]
    public void SettingTheLabelsTakesAListAndRefusesWhatCannotTravel()
    {
        string root = TemporaryRoot();
        try
        {
            using GameRuntime runtime = Bound(root, out var surface, out _, out _);
            using (surface)
            {
                Assert.True(surface.Network.SetTags(["healer", " tank "]));
                Assert.True(surface.Network.SetTags([]));
                Assert.False(surface.Network.SetTags(null!));
                Assert.False(surface.Network.SetTags(
                    [new string('t', LocalPluginPeerRegistry.MaximumTagLength + 1)]));
            }
        }
        finally
        {
            Delete(root);
        }
    }

    /// <summary>The step the plugin tick carries.</summary>
    private const double TickSeconds = 0.015d;

    private static readonly DateTimeOffset Noon =
        new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Ticks until this client looks at the peers' notes again. The client
    /// polls on a period of its own rather than on every tick, so a scenario
    /// that wrote a second note has to let that period go by.
    /// </summary>
    private static void Poll(WorldEvents events)
    {
        double ticks =
            (LocalPluginPeerRegistry.CommandPollPeriod.TotalSeconds
                / TickSeconds) + 2d;
        for (int tick = 0; tick < (int)ticks; tick++)
            events.FireTick(TickSeconds);
    }

    /// <summary>
    /// A verb on this surface's own command registry, which is where a line
    /// the player types arrives, and what it was given.
    /// </summary>
    private static List<string> Verb(RuntimeAutomationSurface surface)
    {
        var ran = new List<string>();
        surface.PluginCommands.Register(
            "example", command => ran.Add(command.RawText));
        return ran;
    }

    /// <summary>
    /// A runtime with a character and a plugin surface whose notes go to a
    /// scratch folder rather than the player's own, ticked by a real event
    /// sink so the client's own delivery runs.
    /// </summary>
    private static GameRuntime Bound(
        string root,
        out RuntimeAutomationSurface surface,
        out LocalPluginPeerRegistry peers,
        out WorldEvents events,
        uint playerObjectId = LocalPlayer,
        TimeProvider? time = null,
        Guid? instanceId = null)
    {
        GameRuntime runtime = GameRuntimeTestFactory.Create();
        runtime.PlayerIdentity.ServerGuid = playerObjectId;
        peers = new LocalPluginPeerRegistry(root, time, instanceId);
        events = new WorldEvents();
        RuntimeAutomationSurface built = new(events, peers);
        surface = built;
        surface.Bind(
            runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        // Where a typed line goes in on either client: the shared router
        // over a chat command surface that asks this surface's own registry
        // for its verbs. A delivered broadcast comes in by the same door, so
        // a test of the delivery has to have that door.
        var bus = new AcDream.Runtime.Chat.LiveChatCommandSurface(
            line => built.TryHandlePluginCommand(line),
            line => built.InterceptChatInput(line));
        surface.BindSubmit(line =>
            AcDream.Runtime.Chat.ChatCommandRouter.Submit(
                line,
                new AcDream.Runtime.Chat.RuntimeChatCommandFeedback(
                    runtime.CommunicationOwner),
                bus,
                AcDream.Runtime.Chat.ChatChannelKind.Say)
            is not (AcDream.Runtime.Chat.SubmitOutcome.Empty
                or AcDream.Runtime.Chat.SubmitOutcome.UnknownCommand
                or AcDream.Runtime.Chat.SubmitOutcome.Dropped));
        return runtime;
    }

    /// <summary>
    /// A client's note. The world name is empty because a runtime that never
    /// chose a character on a server has no world name, and the rule is that
    /// a peer naming a DIFFERENT world is skipped.
    /// </summary>
    private static PluginNetworkClient Note(
        uint clientId,
        uint playerId,
        string name,
        IReadOnlyList<string> tags) => new(
            clientId,
            playerId,
            name,
            string.Empty,
            new PluginNavigationPosition(0x7F7F0001u, 96d, 97d, 1d, 0f, true),
            tags,
            100u, 100u, 100u, 100u, 100u, 100u,
            0f);

    /// <summary>
    /// The instance id a client in these scenarios runs under; the number is
    /// also its client id, which is what the stagger orders clients by.
    /// </summary>
    private static Guid Instance(int which) =>
        Guid.Parse($"{which:D8}-0000-0000-0000-000000000000");

    private static string TemporaryRoot() => Path.Combine(
        Path.GetTempPath(),
        $"acdream-peer-commands-{Guid.NewGuid():N}");

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class ManualTime(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
