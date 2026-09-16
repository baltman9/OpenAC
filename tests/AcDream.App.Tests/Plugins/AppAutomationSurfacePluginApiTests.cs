using AcDream.App.Plugins;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Plugins;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Runtime.Session;
using System.Net;

namespace AcDream.App.Tests.Plugins;

public sealed class AppAutomationSurfacePluginApiTests
{
    [Fact]
    public void ChatReceivedFiresInArrivalOrderWithTheTextClassAndTime()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<PluginChatMessage>();
        surface.Chat.Received += seen.Add;
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        runtime.CommunicationOwner.Chat.OnSystemMessage("first", 0x0Du);
        runtime.CommunicationOwner.Chat.OnCombatLine(
            "second",
            logTextType: 0x0Eu,
            kind: CombatLineKind.Warning);

        Assert.Equal(["first", "second"], seen.Select(static m => m.Text));
        Assert.True(seen[1].Sequence > seen[0].Sequence);
        Assert.Equal(0x0D, seen[0].LogTextType);
        Assert.Equal(0x0E, seen[1].LogTextType);
        Assert.Equal(0, seen[0].CombatKind);
        Assert.Equal(2, seen[1].CombatKind);
        Assert.True(seen[0].Received >= before);
    }

    [Fact]
    public void AFilteredLineReachesNeitherTheTranscriptNorTheEventNorTheRing()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<string>();
        surface.Chat.Received += message => seen.Add(message.Text);
        using IDisposable filter = surface.Chat.RegisterFilter(
            static candidate => candidate.Text.Contains(
                "drop me",
                StringComparison.Ordinal));

        runtime.CommunicationOwner.Chat.OnSystemMessage("please drop me", 0u);
        runtime.CommunicationOwner.Chat.OnSystemMessage("keep me", 0u);

        Assert.Equal(["keep me"], seen);
        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("keep me", entry.Text);
        PluginChatMessage captured = Assert.Single(surface.CaptureMessages(0));
        Assert.Equal("keep me", captured.Text);
    }

    [Fact]
    public void AFilterInstalledBeforeLoginStillAppliesToTheNextSession()
    {
        using var surface = new AppAutomationSurface();
        using IDisposable filter = surface.Chat.RegisterFilter(static _ => true);

        using var runtime = GameRuntimeTestFactory.Create();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        runtime.CommunicationOwner.Chat.OnSystemMessage("dropped", 0u);

        Assert.Equal(0, runtime.CommunicationOwner.Chat.Count);
    }

    [Fact]
    public void UnbindingRemovesTheSurfaceFiltersFromTheSessionsLog()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        using IDisposable filter = surface.Chat.RegisterFilter(static _ => true);

        surface.Unbind();
        runtime.CommunicationOwner.Chat.OnSystemMessage("kept", 0u);

        Assert.Equal(1, runtime.CommunicationOwner.Chat.Count);
    }

    [Fact]
    public void PostMessageUsesTheRequestedTextClass()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.Chat.PostMessage("A tinted line.", (int)RetailLogTextType.Magic);

        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("A tinted line.", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Magic, entry.LogTextType);
    }

    [Fact]
    public void PostMessageRejectsTheStatusOnlyClientLocalClassAndFallsBackToDefault()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.Chat.PostMessage(
            "should not become a status notice",
            (int)RetailLogTextType.ClientLocal);

        // ClientLocal routes to the status overlay, not the transcript, and
        // is re-offered to status filters. A plugin has no legitimate reason
        // to post there, so the surface falls back to the default class.
        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("should not become a status notice", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
        runtime.CommunicationOwner.SpewBox.Tick(0);
        Assert.Equal(0, runtime.CommunicationOwner.SpewBox.Count);
    }

    [Fact]
    public void PostMessageRejectsAnOutOfRangeTextClassAndFallsBackToDefault()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.Chat.PostMessage("out of range", -1);

        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
    }

    [Fact]
    public void PostSystemMessageThrowsOnNullText()
    {
        using var surface = new AppAutomationSurface();

        Assert.Throws<ArgumentNullException>(() => surface.Chat.PostSystemMessage(null!));
    }

    [Fact]
    public void PostSystemMessageIgnoresAnEmptyString()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.Chat.PostSystemMessage(string.Empty);

        Assert.Empty(runtime.CommunicationOwner.Chat.Snapshot());
    }

    [Fact]
    public void LoginCompleteFiresOnEachInWorldEdgeAndLogoffOnEachExit()
    {
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new AppAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int logins = 0;
        int logoffs = 0;
        events.LoginComplete += () => logins++;
        events.Logoff += () => logoffs++;

        Enter(runtime, commands);
        Assert.Equal(1, logins);

        Leave(runtime, commands);
        Assert.Equal(1, logoffs);

        // A reconnect keeps the same plugin instance, so the edge must fire
        // again rather than only once per process.
        Enter(runtime, commands);
        Assert.Equal(2, logins);
        Assert.Equal(1, logoffs);
    }

    [Fact]
    public void RepeatedInWorldReportsDoNotFireLoginCompleteTwice()
    {
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new AppAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int logins = 0;
        events.LoginComplete += () => logins++;

        Enter(runtime, commands);
        Enter(runtime, commands);

        Assert.Equal(1, logins);
    }

    [Fact]
    public void TheDeathMessageReachesPluginsWithoutMatchingChatText()
    {
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var deaths = new List<string>();
        events.LocalPlayerDied += deaths.Add;

        runtime.CommunicationOwner.ReportLocalPlayerDeath("You have died!");

        Assert.Equal(["You have died!"], deaths);
    }

    [Fact]
    public void UnbindingStopsDeathAndLifecycleReports()
    {
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new AppAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int logins = 0;
        var deaths = new List<string>();
        events.LoginComplete += () => logins++;
        events.LocalPlayerDied += deaths.Add;

        surface.Unbind();
        Enter(runtime, commands);
        runtime.CommunicationOwner.ReportLocalPlayerDeath("You have died!");

        Assert.Equal(0, logins);
        Assert.Empty(deaths);
    }

    [Fact]
    public void TheSpellCatalogExposesTheWholeTableAndFindsSpellsByName()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        runtime.CharacterOwner.Spellbook.InstallMetadata(SpellTable.Create(
        [
            Spell(1u, "Strength Self VI"),
            Spell(2u, "Heal Self VII"),
        ]));

        // Nothing is learned, so the known lists stay empty while the table
        // is fully visible.
        Assert.Empty(surface.Spells.KnownSelfBuffs);
        Assert.Equal(2, surface.Spells.All.Count);
        Assert.Same(surface.Spells.All, surface.Spells.All);

        Assert.True(surface.Spells.TryFindByName(
            "heal self vii",
            partialMatch: false,
            out PluginSpellInfo exact));
        Assert.Equal(2u, exact.SpellId);

        Assert.False(surface.Spells.TryFindByName(
            "Heal Self",
            partialMatch: false,
            out _));
        Assert.True(surface.Spells.TryFindByName(
            "Heal Self",
            partialMatch: true,
            out PluginSpellInfo partial));
        Assert.Equal(2u, partial.SpellId);

        Assert.False(surface.Spells.TryFindByName(
            "Nonexistent",
            partialMatch: true,
            out _));
    }

    [Fact]
    public void ServerPopulationIsUnknownUntilTheServerReportsIt()
    {
        using var surface = new AppAutomationSurface();
        Assert.Equal(-1, surface.Character.ServerPopulation);
    }

    [Fact]
    public void ServerPopulationReachesTheSurfaceFromTheLoginTimeWorldNameMessage()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        runtime.Session.CharacterSelectionState.ApplyWorldName(
            "Thistledown",
            serverPopulation: 274);

        Assert.Equal(274, surface.Character.ServerPopulation);
    }

    [Fact]
    public void ObjectChangedMapsEntityAndInventoryDeltasToPluginKinds()
    {
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<PluginObjectChange>();
        events.ObjectChanged += seen.Add;
        var observer = (IRuntimeEventObserver)surface;
        RuntimeEventStamp stamp = default;

        observer.OnEntity(new RuntimeEntityDelta(
            stamp,
            RuntimeEntityChange.Registered,
            new RuntimeEntitySnapshot(
                new RuntimeEntityIdentity(100u, 1u, 1), 0u, 0u, null)));
        observer.OnEntity(new RuntimeEntityDelta(
            stamp,
            RuntimeEntityChange.Rebucketed,
            new RuntimeEntitySnapshot(
                new RuntimeEntityIdentity(100u, 1u, 1), 0u, 0u, null)));
        observer.OnEntity(new RuntimeEntityDelta(
            stamp,
            RuntimeEntityChange.Withdrawn,
            new RuntimeEntitySnapshot(
                new RuntimeEntityIdentity(100u, 1u, 1), 0u, 0u, null)));
        observer.OnEntity(new RuntimeEntityDelta(
            stamp,
            RuntimeEntityChange.Hidden,
            new RuntimeEntitySnapshot(
                new RuntimeEntityIdentity(100u, 1u, 1), 0u, 0u, null)));
        observer.OnInventory(new RuntimeInventoryDelta(
            stamp,
            RuntimeInventoryChange.Added,
            new RuntimeInventoryItemSnapshot(
                200u, 1, "Item", 0u, 0, 0u, 0u, 0, 0)));
        observer.OnInventory(new RuntimeInventoryDelta(
            stamp,
            RuntimeInventoryChange.Removed,
            new RuntimeInventoryItemSnapshot(
                200u, 1, "Item", 0u, 0, 0u, 0u, 0, 0)));
        observer.OnInventory(new RuntimeInventoryDelta(
            stamp,
            RuntimeInventoryChange.Cleared,
            default));

        Assert.Equal(
            new (uint ObjectId, PluginObjectChangeKind Kind)[]
            {
                (100u, PluginObjectChangeKind.Created),
                (100u, PluginObjectChangeKind.Moved),
                (100u, PluginObjectChangeKind.Released),
                (100u, PluginObjectChangeKind.Updated),
                (200u, PluginObjectChangeKind.Created),
                (200u, PluginObjectChangeKind.Released),
            },
            seen.Select(static c => (c.ObjectId, c.Kind)));
    }

    [Fact]
    public void ContainerOpenedAndClosedFireOnExternalContainerTransitions()
    {
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        uint? opened = null;
        uint? closed = null;
        events.ContainerOpened += id => opened = id;
        events.ContainerClosed += id => closed = id;

        runtime.InventoryOwner.ExternalContainers.RequestOpen(500u);
        runtime.InventoryOwner.ExternalContainers.ApplyViewContents(500u);
        Assert.Equal(500u, opened);
        Assert.Null(closed);

        runtime.InventoryOwner.ExternalContainers.ApplyClose(500u);
        Assert.Equal(500u, closed);
    }

    [Fact]
    public void ObjectChangedReportsIdentReceivedOnBothTheFirstResponseAndARefresh()
    {
        // AcceptAppraisalResponse now raises AppraisalReceived (and so this
        // ObjectChanged) on every accepted response, not just the first --
        // a refresh of already-held data can still carry a changed payload
        // (durability, stack count) that observers need to see.
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<PluginObjectChange>();
        events.ObjectChanged += seen.Add;

        runtime.ActionOwner.Transactions.TryRequestAppraisal(
            700u, static _ => { });
        runtime.ActionOwner.Transactions.AcceptAppraisalResponse(700u);
        runtime.ActionOwner.Transactions.AcceptAppraisalResponse(700u);

        Assert.Equal(
            2,
            seen.Count(c =>
                c.ObjectId == 700u
                    && c.Kind == PluginObjectChangeKind.IdentReceived));
    }

    [Fact]
    public void LogoutIsUnavailableWithoutAnInWorldSessionAndNeverCallsTheRoute()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int calls = 0;
        surface.BindLogout(() =>
        {
            calls++;
            return true;
        });

        // The runtime never reached RuntimeLifecycleState.InWorld in this
        // fixture (that requires a driven session), so IsAvailable is false
        // and Logout must refuse without ever touching the bound route --
        // matching every other automation command's IsAvailable gate.
        Assert.False(((ILoginAutomation)surface).Logout());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DialogsAnswerForwardsToTheBoundRoute()
    {
        using var surface = new AppAutomationSurface();
        uint? seenContext = null;
        bool? seenAccept = null;
        surface.BindDialogs((contextId, accept) =>
        {
            seenContext = contextId;
            seenAccept = accept;
            return true;
        });

        bool result = ((IDialogAutomation)surface).Answer(42u, true);

        Assert.True(result);
        Assert.Equal(42u, seenContext);
        Assert.True(seenAccept);
    }

    [Fact]
    public void DialogsAnswerReturnsFalseWithoutABoundRoute()
    {
        using var surface = new AppAutomationSurface();

        Assert.False(((IDialogAutomation)surface).Answer(1u, true));
    }

    [Fact]
    public void RaiseConfirmationRequestedFiresTheEvent()
    {
        var events = new WorldEvents();
        using var surface = new AppAutomationSurface(events);
        PluginConfirmation? seen = null;
        events.ConfirmationRequested += c => seen = c;

        surface.RaiseConfirmationRequested(
            new PluginConfirmation(7u, 5, "Continue?"));

        Assert.Equal(new PluginConfirmation(7u, 5, "Continue?"), seen);
    }

    // Enter/Leave drive a real GameRuntime + LiveSessionController +
    // LiveSessionHost + DirectGameRuntimeCommandAdapter through Start()/Stop()
    // -- the exact production command boundary the headless host uses --
    // rather than fabricating an EmitLifecycle call directly. OnLifecycle
    // must fire off the same real path production hosts use
    // (GameRuntime.SyncLifecycleEmission), not off a synthetic delta a real
    // session would never produce on its own.
    private static (GameRuntime Runtime, DirectGameRuntimeCommandAdapter Commands) CreateRealSession()
    {
        var operations = new RealSessionOperations();
        GameRuntime runtime = GameRuntimeTestFactory.Create(session: operations);
        var session = new LiveSessionHost(
            runtime.Session,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new NoOpEventRoute(),
                    _ => new NoOpCommandRoute()),
                _ => { },
                new LiveSessionSelectionBindings(
                    id => runtime.PlayerIdentity.ServerGuid = id,
                    _ => { },
                    _ => { },
                    _ => { },
                    _ => { },
                    () => { }),
                new LiveSessionEnteredWorldBindings(
                    _ => { },
                    () => { },
                    () => { },
                    _ => { },
                    () => { }),
                (_, _, _) => { },
                () => { },
                _ => { },
                _ => { }),
            new LiveSessionConnectOptions(
                true,
                "127.0.0.1",
                9000,
                "plugin-api-user",
                "plugin-api-password"),
            runtime: runtime);
        var commands = new DirectGameRuntimeCommandAdapter(runtime, session);
        return (runtime, commands);
    }

    private static void Enter(GameRuntime runtime, DirectGameRuntimeCommandAdapter commands) =>
        commands.Start(runtime.Generation);

    private static void Leave(GameRuntime runtime, DirectGameRuntimeCommandAdapter commands) =>
        commands.Stop(runtime.Generation);

    private sealed class RealSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            new(endpoint, new NoOpTransport());

        public void Connect(WorldSession session, string user, string password) { }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [new CharacterList.Character(0x50000001u, "PluginApiFixture", 0u)],
                [],
                11,
                "PluginApi",
                true,
                true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex) { }

        public void Tick(WorldSession session) { }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class NoOpTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }
        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }

        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            from = null;
            return -1;
        }

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);

        public void Dispose() { }
    }

    private sealed class NoOpEventRoute : ILiveSessionEventRouting
    {
        public void Attach() { }
        public void Dispose() { }
    }

    private sealed class NoOpCommandRoute : ILiveSessionCommandRouting
    {
        public void Activate() { }
        public void Dispose() { }
    }

    private static SpellMetadata Spell(uint spellId, string name) =>
        new(
            SpellId: spellId,
            Name: name,
            School: "Life",
            Family: 0u,
            IconId: 0u,
            SpellWords: "",
            Duration: 60f,
            ManaCost: 0,
            IsDebuff: false,
            IsFellowship: false,
            Description: "",
            SortKey: 0,
            Difficulty: 0,
            Flags: 0u,
            Generation: 1,
            IsFastWindup: false,
            IsOffensive: false,
            IsUntargeted: false,
            Speed: 0f,
            CasterEffect: 0u,
            TargetEffect: 0u,
            TargetMask: 0u,
            SpellType: 0);
}
