using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// What a plugin reading chat actually receives, taken from the real
/// ingestion route rather than a hand-built message.
/// </summary>
/// <remarks>
/// A plugin that has to recognise one particular line — an NPC's answer, a
/// server notice — cannot do it from the message's kind, which only says where
/// the line came from: an ordinary tell and an NPC's tell are the same kind,
/// and half the lines the server writes share one kind between them. The
/// log-text type is the value that separates them, and it is the one the
/// client colours the line by, so it has to reach the plugin surface intact.
/// </remarks>
public sealed class PluginChatLogTextTypeTests
{
    [Fact]
    public void AnIngestedTellReachesThePluginWithItsLogTextTypeAndSenderApart()
    {
        using GameRuntime runtime = Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        runtime.CommunicationOwner.Chat.OnTellReceived(
            "Aun Tanua",
            "Greetings.",
            senderGuid: 0x50000002u,
            logTextType: 0x03u);

        PluginChatMessage message = Assert.Single(surface.CaptureMessages(0UL));
        Assert.Equal(0x03, message.LogTextType);
        Assert.Equal("Aun Tanua", message.Sender);
        // The message stays bare: the plugin gets sender and text apart, and
        // the line the chat window shows is composed from the two.
        Assert.Equal("Greetings.", message.Text);
    }

    [Fact]
    public void AnIngestedServerLineReachesThePluginWithItsLogTextTypeAndWholeText()
    {
        using GameRuntime runtime = Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        runtime.CommunicationOwner.Chat.OnSystemMessage(
            "Aun Tanua gives you a Token.",
            chatType: 0x00u);
        // A second server line of a different type, so the pin holds the value
        // that travelled rather than the zero a dropped field would leave.
        runtime.CommunicationOwner.Chat.OnSystemMessage(
            "You have entered the Allegiance channel.",
            chatType: 0x12u);

        IReadOnlyList<PluginChatMessage> messages = surface.CaptureMessages(0UL);
        Assert.Equal(2, messages.Count);
        Assert.Equal(0x00, messages[0].LogTextType);
        Assert.Equal("Aun Tanua gives you a Token.", messages[0].Text);
        Assert.Equal(string.Empty, messages[0].Sender);
        Assert.Equal(0x12, messages[1].LogTextType);
    }

    private static GameRuntime Create()
    {
        var operations = new Operations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations));
    }

    private sealed class Operations :
        IRuntimeCombatAttackOperations,
        IRuntimeCombatTargetOperations,
        IRuntimeCombatModeOperations,
        IRuntimeSpellCastOperations
    {
        public bool CanStartAttack(bool allowAutoTarget) => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power, bool allowAutoTarget) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
