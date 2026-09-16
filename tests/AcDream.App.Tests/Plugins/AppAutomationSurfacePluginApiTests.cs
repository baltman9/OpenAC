using AcDream.App.Plugins;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Plugins;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;

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
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int logins = 0;
        int logoffs = 0;
        events.LoginComplete += () => logins++;
        events.Logoff += () => logoffs++;

        Enter(runtime);
        Assert.Equal(1, logins);

        Leave(runtime);
        Assert.Equal(1, logoffs);

        // A reconnect keeps the same plugin instance, so the edge must fire
        // again rather than only once per process.
        Enter(runtime);
        Assert.Equal(2, logins);
        Assert.Equal(1, logoffs);
    }

    [Fact]
    public void RepeatedInWorldReportsDoNotFireLoginCompleteTwice()
    {
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int logins = 0;
        events.LoginComplete += () => logins++;

        Enter(runtime);
        Enter(runtime);

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
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int logins = 0;
        var deaths = new List<string>();
        events.LoginComplete += () => logins++;
        events.LocalPlayerDied += deaths.Add;

        surface.Unbind();
        Enter(runtime);
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

    private static void Enter(GameRuntime runtime) =>
        runtime.EventSink.EmitLifecycle(
            RuntimeLifecycleState.Starting,
            RuntimeLifecycleState.InWorld);

    private static void Leave(GameRuntime runtime) =>
        runtime.EventSink.EmitLifecycle(
            RuntimeLifecycleState.InWorld,
            RuntimeLifecycleState.Stopping);

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
