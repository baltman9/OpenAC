using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Headless.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;

namespace AcDream.Headless.Tests;

public sealed class HeadlessPluginApiSurfaceTests
{
    [Fact]
    public void ChatReceivedFiresInOrderWithTheTextClassAndCombatKind()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var seen = new List<PluginChatMessage>();
        host.Automation.Chat.Received += seen.Add;

        runtime.CommunicationOwner.Chat.OnSystemMessage("first", 0x0Du);
        runtime.CommunicationOwner.Chat.OnCombatLine(
            "second",
            logTextType: 0x0Eu,
            kind: CombatLineKind.Error);

        Assert.Equal(["first", "second"], seen.Select(static m => m.Text));
        Assert.Equal(0x0D, seen[0].LogTextType);
        Assert.Equal(0, seen[0].CombatKind);
        Assert.Equal(3, seen[1].CombatKind);
    }

    [Fact]
    public void AFilteredLineNeverReachesTheTranscriptOrTheEvent()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var seen = new List<string>();
        host.Automation.Chat.Received += message => seen.Add(message.Text);
        using IDisposable filter = host.Automation.Chat.RegisterFilter(
            static candidate => candidate.Text == "drop me");

        runtime.CommunicationOwner.Chat.OnSystemMessage("drop me", 0u);
        runtime.CommunicationOwner.Chat.OnSystemMessage("keep me", 0u);

        Assert.Equal(["keep me"], seen);
        Assert.Equal(1, runtime.CommunicationOwner.Chat.Count);
    }

    [Fact]
    public void LoginCompleteAndLogoffFollowTheInWorldEdge()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        int logins = 0;
        int logoffs = 0;
        host.Events.LoginComplete += () => logins++;
        host.Events.Logoff += () => logoffs++;

        runtime.EventSink.EmitLifecycle(
            RuntimeLifecycleState.Starting,
            RuntimeLifecycleState.InWorld);
        runtime.EventSink.EmitLifecycle(
            RuntimeLifecycleState.InWorld,
            RuntimeLifecycleState.Stopping);
        runtime.EventSink.EmitLifecycle(
            RuntimeLifecycleState.Starting,
            RuntimeLifecycleState.InWorld);

        Assert.Equal(2, logins);
        Assert.Equal(1, logoffs);
    }

    [Fact]
    public void TheDeathMessageReachesPlugins()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var deaths = new List<string>();
        host.Events.LocalPlayerDied += deaths.Add;

        runtime.CommunicationOwner.ReportLocalPlayerDeath("You have died!");

        Assert.Equal(["You have died!"], deaths);
    }

    [Fact]
    public void PostMessageUsesTheRequestedTextClass()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        host.Automation.Chat.PostMessage("tinted", (int)RetailLogTextType.Magic);

        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("tinted", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Magic, entry.LogTextType);
    }

    [Fact]
    public void PostMessageRejectsTheStatusOnlyClientLocalClassAndFallsBackToDefault()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        host.Automation.Chat.PostMessage(
            "should not become a status notice",
            (int)RetailLogTextType.ClientLocal);

        // ClientLocal routes to the status overlay, not the transcript.
        // A plugin has no legitimate reason to post there, so the surface
        // must fall back to the default transcript class instead.
        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("should not become a status notice", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
        runtime.CommunicationOwner.SpewBox.Tick(0);
        Assert.Equal(0, runtime.CommunicationOwner.SpewBox.Count);
    }

    [Fact]
    public void PostMessageRejectsAnOutOfRangeTextClassAndFallsBackToDefault()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        host.Automation.Chat.PostMessage("out of range", 9999);

        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
    }

    [Fact]
    public void PostSystemMessageThrowsOnNullText()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        Assert.Throws<ArgumentNullException>(
            () => host.Automation.Chat.PostSystemMessage(null!));
    }

    [Fact]
    public void AHostWithoutAWindowReportsNoClipboard()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        IPluginHost asHost = host;
        Assert.False(asHost.Clipboard.TrySetText("anything"));
    }

    [Fact]
    public void FileStorageReportsTheDirectoryItWritesTo()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "acdream-storage-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            IPluginStorage storage = new FilePluginStorage(root);

            Assert.Equal(Path.GetFullPath(root), storage.RootPath);
            Assert.Null(((IPluginStorage)NoOpPluginStorage.Instance).RootPath);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static GameRuntime NewRuntime()
    {
        var operations = new InertOperations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations));
    }

    private static HeadlessPluginHost NewHost(GameRuntime runtime) =>
        new(runtime, new InertLogger());

    private sealed class InertLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? error = null) { }
    }

    private sealed class InertOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<AcDream.Core.Items.ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId,
            AcDream.Core.Spells.SpellMetadata spell,
            bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
