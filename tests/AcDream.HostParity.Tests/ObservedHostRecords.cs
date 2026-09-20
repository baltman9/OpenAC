using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.App.Plugins;
using AcDream.Headless.Plugins;
using AcDream.Runtime;
using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The records each host really builds, built here so the census reads the
/// hosts' own code instead of a list written beside it.
///
/// Neither host can be started without, respectively, a window and a world
/// connection, so the parts are handed in as bare instances that are never
/// called: a capability builder only turns a part into a delegate or passes
/// the reference on, and what this file needs to know is which capabilities
/// came out of that, not what they do. Anything that would read a part while
/// building -- a data table, for instance -- has to be deferred in the
/// builder, which is where it belongs anyway.
/// </summary>
internal static class ObservedHostRecords
{
    /// <summary>A part that exists but is never called.</summary>
    private static T Part<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    /// <summary>
    /// The windowed host's capability record with every part present, which
    /// is the most it can ever supply.
    /// </summary>
    internal static RuntimeAutomationHostCapabilities WindowedCapabilities() =>
        GraphicalAutomationCapabilities.Build(new GraphicalAutomationParts
        {
            Runtime = Part<GameRuntime>(),
            Warn = static _ => { },
            Content = Part<AcDream.Content.RuntimeDatCollection>(),
            MagicCatalog = Part<AcDream.Content.MagicCatalog>(),
            SessionCommands = Part<AcDream.App.Runtime.CurrentGameRuntimeAdapter>(),
            NavigationWalk = Part<AcDream.Runtime.Navigation.NavigationWalkController>(),
            Items = Part<AcDream.Runtime.Gameplay.RuntimeItemInteraction>(),
            Teleport = Part<AcDream.App.Streaming.LocalPlayerTeleportController>(),
            RetainedUi = Part<AcDream.App.UI.RetailUiRuntime>(),
            Selection = Part<AcDream.App.Interaction.SelectionInteractionController>(),
            EntityDeletion = Part<AcDream.App.World.LiveEntityDeletionController>(),
            Input = Part<AcDream.UI.Abstractions.Input.InputDispatcher>(),
        });

    /// <summary>
    /// The windowless host's capability record with every part present, which
    /// is the most it can ever supply.
    /// </summary>
    internal static RuntimeAutomationHostCapabilities WindowlessCapabilities() =>
        HeadlessAutomationCapabilities.Build(new HeadlessAutomationParts
        {
            Runtime = Part<GameRuntime>(),
            Warn = static _ => { },
            Content = Part<AcDream.Content.RuntimeDatCollection>(),
            MagicCatalog = Part<AcDream.Content.MagicCatalog>(),
            SubmitChatText = static _ => true,
            SessionCommands = Part<AcDream.App.Runtime.CurrentGameRuntimeAdapter>(),
            NavigationWalk = Part<AcDream.Runtime.Navigation.NavigationWalkController>(),
            Items = Part<AcDream.Headless.Plugins.HeadlessItemAutomation>(),
            Logout = Part<AcDream.Headless.Hosting.HeadlessLogoutAutomation>(),
            AnswerConfirmation = static (_, _) => true,
        });

    internal static RuntimeAutomationHostCapabilities CapabilitiesFor(string host) =>
        host == ParityHost.Windowed
            ? WindowedCapabilities()
            : WindowlessCapabilities();

    /// <summary>
    /// The windowed host's dependency record with every optional dependency
    /// present, which is the most it can ever supply.
    /// </summary>
    internal static GameRuntimeDependencies WindowedRuntimeDependencies() =>
        GraphicalAutomationCapabilities.BuildRuntimeDependencies(
            Part<AcDream.App.Combat.CombatAttackOperationsSlot>(),
            Part<AcDream.App.Combat.RuntimeCombatTargetOperationsSlot>(),
            Part<AcDream.App.Combat.RuntimeCombatModeOperationsSlot>(),
            Part<AcDream.App.Spells.RuntimeSpellCastOperationsSlot>(),
            timeSyncDiagnostic: static _ => { });

    /// <summary>
    /// The windowless host's dependency record with every optional dependency
    /// present, which is the most it can ever supply.
    /// </summary>
    internal static GameRuntimeDependencies WindowlessRuntimeDependencies() =>
        HeadlessAutomationCapabilities.BuildRuntimeDependencies(
            Part<AcDream.Headless.Hosting.HeadlessGameplayOperations>(),
            TimeProvider.System,
            static _ => { },
            Part<AcDream.Runtime.Session.ProductionLiveSessionOperations>(),
            static () => 0d);

    internal static GameRuntimeDependencies RuntimeDependenciesFor(string host) =>
        host == ParityHost.Windowed
            ? WindowedRuntimeDependencies()
            : WindowlessRuntimeDependencies();

    internal static IReadOnlyDictionary<string, string> ConditionalCapabilities(
        string host) =>
        host == ParityHost.Windowed
            ? GraphicalAutomationCapabilities.Conditional
            : HeadlessAutomationCapabilities.Conditional;

    internal static IReadOnlyDictionary<string, string>
        ConditionalRuntimeDependencies(string host) =>
        host == ParityHost.Windowed
            ? GraphicalAutomationCapabilities.ConditionalRuntimeDependencies
            : HeadlessAutomationCapabilities.ConditionalRuntimeDependencies;

    /// <summary>
    /// Which members of a dependency record this run really filled in. Only
    /// the ones that can be left out are interesting: a member with no
    /// meaningful empty value is the same on every host by construction.
    /// </summary>
    internal static IReadOnlySet<string> SuppliedMembers(object record)
    {
        var supplied = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyInfo property in record.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name == "EqualityContract")
                continue;
            if (property.PropertyType.IsValueType)
                continue;
            if (property.GetValue(record) is not null)
                supplied.Add(property.Name);
        }
        return supplied;
    }

}
