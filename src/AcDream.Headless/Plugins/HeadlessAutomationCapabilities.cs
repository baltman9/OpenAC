using AcDream.Runtime;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Plugins;

/// <summary>
/// What the windowless host can lend the plugin surface. The declared set is
/// a standing claim the host-parity census reads without starting a session;
/// the binding pass refuses a record that supplies anything missing from it,
/// so the claim cannot drift away from the record below it.
/// </summary>
internal static class HeadlessAutomationCapabilities
{
    internal static IReadOnlySet<string> Declared { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(RuntimeAutomationHostCapabilities.Content),
            nameof(RuntimeAutomationHostCapabilities.MagicCatalog),
            nameof(RuntimeAutomationHostCapabilities.SubmitChatText),
            nameof(RuntimeAutomationHostCapabilities.SessionCommands),
            nameof(RuntimeAutomationHostCapabilities.NavigationWalk),
            nameof(RuntimeAutomationHostCapabilities.Equipment),
            nameof(RuntimeAutomationHostCapabilities.Items),
            nameof(RuntimeAutomationHostCapabilities.Logout),
            nameof(RuntimeAutomationHostCapabilities.AnswerConfirmation),
            nameof(RuntimeAutomationHostCapabilities.RemoteBodiesUnsimulated),
        };

    /// <summary>Which runtime dependencies this host fills in.</summary>
    internal static IReadOnlySet<string> DeclaredRuntimeDependencies { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(GameRuntimeDependencies.CombatAttackOperations),
            nameof(GameRuntimeDependencies.CombatTargetOperations),
            nameof(GameRuntimeDependencies.CombatModeOperations),
            nameof(GameRuntimeDependencies.SpellCastOperations),
            nameof(GameRuntimeDependencies.Log),
            nameof(GameRuntimeDependencies.TimeProvider),
            nameof(GameRuntimeDependencies.SessionOperations),
            nameof(GameRuntimeDependencies.CombatTime),
        };

    /// <summary>Which live-session host bindings this host fills in.</summary>
    internal static IReadOnlySet<string> DeclaredSessionHostBindings { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LiveSessionHostBindings.Routing),
            nameof(LiveSessionHostBindings.Reset),
            nameof(LiveSessionHostBindings.Selection),
            nameof(LiveSessionHostBindings.EnteredWorld),
            nameof(LiveSessionHostBindings.Connecting),
            nameof(LiveSessionHostBindings.Connected),
            nameof(LiveSessionHostBindings.Roster),
            nameof(LiveSessionHostBindings.CharacterEntered),
            nameof(LiveSessionHostBindings.LoginCommands),
            nameof(LiveSessionHostBindings.CharacterCreated),
            nameof(LiveSessionHostBindings.CreationFailed),
            nameof(LiveSessionSelectionBindings.SetPlayerIdentity),
            nameof(LiveSessionSelectionBindings.SetVitalsIdentity),
            nameof(LiveSessionSelectionBindings.SetChatIdentity),
            nameof(LiveSessionSelectionBindings.MarkPersistent),
            nameof(LiveSessionSelectionBindings.SetVanishProbeIdentity),
            nameof(LiveSessionSelectionBindings.ClearCombat),
            nameof(LiveSessionEnteredWorldBindings.SetActiveCharacter),
            nameof(LiveSessionEnteredWorldBindings.RestoreLayout),
            nameof(LiveSessionEnteredWorldBindings.SyncToolbar),
            nameof(LiveSessionEnteredWorldBindings.LoadCharacterSettings),
            nameof(LiveSessionEnteredWorldBindings.ArmPlayerModeAutoEntry),
        };

    /// <summary>Which character-session bindings this host fills in.</summary>
    internal static IReadOnlySet<string> DeclaredCharacterSessionBindings { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LiveCharacterSessionBindings.Combat),
            nameof(LiveCharacterSessionBindings.Character),
            nameof(LiveCharacterSessionBindings.ResolveSkillFormulaBonus),
            nameof(LiveCharacterSessionBindings.OnConfirmationRequest),
            nameof(LiveCharacterSessionBindings.OnConfirmationDone),
            nameof(LiveCharacterSessionBindings.ClientTime),
            nameof(LiveCharacterSessionBindings.OnCharacterOptionsChanged),
        };
}
