using AcDream.Runtime.Plugins;

namespace AcDream.App.Plugins;

/// <summary>
/// What the windowed host can lend the plugin surface. The declared set is a
/// standing claim the host-parity census reads without opening a window; the
/// binding pass refuses a record that supplies anything missing from it, so
/// the claim cannot drift away from the record the window builds.
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
}
