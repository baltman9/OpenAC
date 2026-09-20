using AcDream.Runtime.Plugins;

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
}
