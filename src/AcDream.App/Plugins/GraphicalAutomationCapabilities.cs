using AcDream.Content;
using AcDream.Runtime;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Plugins;

/// <summary>
/// The window-owned parts the plugin surface is built from. Every one is
/// optional because composition publishes them in stages and a part can be
/// absent in a cut-down run; what a missing part costs a plugin is decided in
/// one place, <see cref="GraphicalAutomationCapabilities.Build"/>, so a test
/// can build the host's real record without opening a window.
/// </summary>
internal sealed record GraphicalAutomationParts
{
    /// <summary>The runtime whose owners answer everything host-independent.</summary>
    public required GameRuntime Runtime { get; init; }

    /// <summary>Where a binding problem is reported.</summary>
    public Action<string>? Warn { get; init; }

    public IDatReaderWriter? Content { get; init; }
    public MagicCatalog? MagicCatalog { get; init; }
    public AcDream.App.Runtime.CurrentGameRuntimeAdapter? SessionCommands { get; init; }
    public AcDream.Runtime.Navigation.NavigationWalkController? NavigationWalk { get; init; }
    public AcDream.Runtime.Gameplay.RuntimeItemInteraction? Items { get; init; }
    public AcDream.App.Streaming.LocalPlayerTeleportController? Teleport { get; init; }
    public AcDream.App.UI.RetailUiRuntime? RetainedUi { get; init; }
    public AcDream.App.Interaction.SelectionInteractionController? Selection { get; init; }
    public AcDream.App.World.LiveEntityDeletionController? EntityDeletion { get; init; }
    public InputDispatcher? Input { get; init; }
}

/// <summary>
/// What the windowed host can lend the plugin surface. The declared set is a
/// standing claim the host-parity census reads without opening a window; the
/// binding pass refuses a record that supplies anything missing from it, and
/// the census builds the record <see cref="Build"/> really makes, so the claim
/// cannot drift away from the code.
/// </summary>
internal static class GraphicalAutomationCapabilities
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
            nameof(RuntimeAutomationHostCapabilities.SalvageItems),
            nameof(RuntimeAutomationHostCapabilities.SellItem),
            nameof(RuntimeAutomationHostCapabilities.Logout),
            nameof(RuntimeAutomationHostCapabilities.AnswerConfirmation),
            nameof(RuntimeAutomationHostCapabilities.UseWorldObject),
            nameof(RuntimeAutomationHostCapabilities.DismissGhost),
            nameof(RuntimeAutomationHostCapabilities.SelectionAction),
            nameof(RuntimeAutomationHostCapabilities.ChatInputActive),
            nameof(RuntimeAutomationHostCapabilities.ChatComposer),
            nameof(RuntimeAutomationHostCapabilities.SpeciesName),
        };

    /// <summary>
    /// Capabilities this host supplies only when something outside the code is
    /// there. Named so the binding pass can tell a configuration apart from a
    /// defect when a seam comes out unfilled.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Conditional { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(RuntimeAutomationHostCapabilities.Content)] =
                "the installed data files were not opened",
            [nameof(RuntimeAutomationHostCapabilities.SpeciesName)] =
                "the creature name table comes from the installed data files",
            [nameof(RuntimeAutomationHostCapabilities.MagicCatalog)] =
                "the spell catalog comes from the installed data files",
        };

    /// <summary>
    /// Builds the record this host really hands the binding pass. The window
    /// only names its parts; every decision about what a missing part costs a
    /// plugin is here, where a test can run it.
    /// </summary>
    internal static RuntimeAutomationHostCapabilities Build(
        GraphicalAutomationParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        AcDream.Runtime.Gameplay.RuntimeItemInteraction? items = parts.Items;
        AcDream.App.Interaction.SelectionInteractionController? selection =
            parts.Selection;
        AcDream.App.UI.RetailUiRuntime? retainedUi = parts.RetainedUi;
        AcDream.App.Runtime.CurrentGameRuntimeAdapter? session =
            parts.SessionCommands;
        AcDream.App.Streaming.LocalPlayerTeleportController? teleport =
            parts.Teleport;
        InputDispatcher? input = parts.Input;
        GameRuntime runtime = parts.Runtime;
        // Read the creature name table the first time a plugin asks for a
        // species, not while the surface is being wired: nothing here should
        // pay for a table a session may never use.
        Func<int, string>? speciesName = null;
        if (parts.Content is { } speciesContent)
        {
            var speciesTable = new Lazy<
                AcDream.App.UI.Layout.CreatureDisplayNameResolver>(
                () => AcDream.App.UI.Layout.CreatureDisplayNameResolver
                    .Load(speciesContent));
            speciesName = species => speciesTable.Value.Resolve(species);
        }
        return new RuntimeAutomationHostCapabilities
        {
            HostName = "windowed",
            Declared = Declared,
            Conditional = Conditional,
            Warn = parts.Warn,
            Content = parts.Content,
            MagicCatalog = parts.MagicCatalog,
            SubmitChatText = session is null ? null : session.SubmitChatText,
            SessionCommands = session,
            NavigationWalk = parts.NavigationWalk,
            SpeciesName = speciesName,
            Equipment = items is null
                ? null
                : new RuntimeAutomationEquipmentCommands(
                    (itemId, requestedLocation) => items.TryWieldItem(
                        itemId,
                        (AcDream.Core.Items.EquipMask)requestedLocation),
                    () => items.IsAutoWieldBusy,
                    items.TryWieldItemSecondary),
            Items = items is null
                ? null
                : new RuntimeAutomationItemCommands(
                    items.TryUseItemForAutomation,
                    items.TryApplyItem,
                    items.TryMoveItemForAutomation,
                    items.TryMergeItemsForAutomation,
                    items.TryDropItemForAutomation,
                    items.TryGiveItemForAutomation,
                    items.PlaceWorldItemInBackpack,
                    items.TryAppraiseForAutomation),
            SalvageItems = items is null
                ? null
                : items.TrySalvageItemsForAutomation,
            SellItem = items is null
                ? null
                : (vendorId, itemId, amount) =>
                    items.TrySell(vendorId, [(amount, itemId)]),
            Logout = new RuntimeAutomationLogoutCommands(
                () => teleport?.TryRequestLogout() == true,
                () => teleport is not null
                    && runtime.Session.IsInWorld
                    && !runtime.TransitOwner.IsLogoutActive
                    && !runtime.TransitOwner.IsTeleportActive
                    && !runtime.TransitOwner.HasPendingTeleportStart),
            AnswerConfirmation = retainedUi is null
                ? null
                : retainedUi.TryAnswerConfirmation,
            UseWorldObject = selection is null
                ? null
                : objectId => RuntimeAutomationSurface.MapWorldObjectUseOutcome(
                    selection.TryUseForAutomation(objectId)),
            DismissGhost = parts.EntityDeletion is not { } deletion
                ? null
                : deletion.DeleteClientGhost,
            SelectionAction = selection is null
                ? null
                : action => selection.HandleInputAction(action switch
                {
                    AcDream.Plugin.Abstractions.PluginSelectionAction
                        .PreviousSelection =>
                        InputAction.SelectionPreviousSelection,
                    AcDream.Plugin.Abstractions.PluginSelectionAction
                        .PreviousPlayer =>
                        InputAction.SelectionPreviousPlayer,
                    AcDream.Plugin.Abstractions.PluginSelectionAction
                        .NextPlayer => InputAction.SelectionNextPlayer,
                    _ => InputAction.None,
                }),
            // Automation that steers by holding keys has to know when the
            // keyboard is going into the chat entry instead of the character.
            ChatInputActive = input is null
                ? null
                : () => input.WantsTextInput,
            ChatComposer = retainedUi is null
                ? null
                : retainedUi.ComposeChatText,
        };
    }

    /// <summary>Which runtime dependencies this host fills in.</summary>
    internal static IReadOnlySet<string> DeclaredRuntimeDependencies { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(GameRuntimeDependencies.CombatAttackOperations),
            nameof(GameRuntimeDependencies.CombatTargetOperations),
            nameof(GameRuntimeDependencies.CombatModeOperations),
            nameof(GameRuntimeDependencies.SpellCastOperations),
            nameof(GameRuntimeDependencies.Log),
            nameof(GameRuntimeDependencies.TimeSyncDiagnostic),
        };

    /// <summary>
    /// Runtime dependencies this host fills in only under a condition, for the
    /// same reason as <see cref="Conditional"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>
        ConditionalRuntimeDependencies { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(GameRuntimeDependencies.TimeSyncDiagnostic)] =
                "a launch option turns the sky dump on",
        };

    /// <summary>
    /// Builds the dependency record this host really constructs the runtime
    /// with, so the census reads the code rather than a list beside it.
    /// </summary>
    internal static GameRuntimeDependencies BuildRuntimeDependencies(
        AcDream.Runtime.Gameplay.IRuntimeCombatAttackOperations attack,
        AcDream.Runtime.Gameplay.IRuntimeCombatTargetOperations target,
        AcDream.Runtime.Gameplay.IRuntimeCombatModeOperations mode,
        AcDream.Runtime.Gameplay.IRuntimeSpellCastOperations spells,
        Action<string>? timeSyncDiagnostic) =>
        new(
            attack,
            target,
            mode,
            spells,
            Log: Console.WriteLine,
            TimeSyncDiagnostic: timeSyncDiagnostic);

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
            nameof(LiveSessionSelectionBindings.ArmLoginTunnel),
            nameof(LiveSessionEnteredWorldBindings.SetActiveCharacter),
            nameof(LiveSessionEnteredWorldBindings.RestoreLayout),
            nameof(LiveSessionEnteredWorldBindings.SyncToolbar),
            nameof(LiveSessionEnteredWorldBindings.LoadCharacterSettings),
            nameof(LiveSessionEnteredWorldBindings.ArmPlayerModeAutoEntry),
            nameof(LiveSessionEnteredWorldBindings.ResumeWorldAudio),
        };

    /// <summary>Which character-session bindings this host fills in.</summary>
    internal static IReadOnlySet<string> DeclaredCharacterSessionBindings { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LiveCharacterSessionBindings.Combat),
            nameof(LiveCharacterSessionBindings.Character),
            nameof(LiveCharacterSessionBindings.ResolveSkillFormulaBonus),
            nameof(LiveCharacterSessionBindings.OnSkillsUpdated),
            nameof(LiveCharacterSessionBindings.OnConfirmationRequest),
            nameof(LiveCharacterSessionBindings.OnConfirmationDone),
            nameof(LiveCharacterSessionBindings.ClientTime),
            nameof(LiveCharacterSessionBindings.OnMovementStatsUpdated),
            nameof(LiveCharacterSessionBindings.OnCharacterOptionsChanged),
        };
}
