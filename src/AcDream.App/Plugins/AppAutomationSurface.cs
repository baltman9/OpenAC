using AcDream.App.Runtime;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.App.Plugins;

/// <summary>
/// The graphical host's plugin automation surface. Every answer and every
/// command is the shared runtime surface's — a plugin sees the same client
/// here as it does in the windowless host. All this adds is the graphical
/// host's own wiring: its command adapter carries the chat-submission route
/// with it, so the host binds one object instead of three.
/// </summary>
internal sealed class AppAutomationSurface : RuntimeAutomationSurface
{
    protected override double EnchantmentTime =>
        System.Diagnostics.Stopwatch.GetTimestamp()
        / (double)System.Diagnostics.Stopwatch.Frequency;

    public AppAutomationSurface()
        : this(events: null)
    {
    }

    internal AppAutomationSurface(
        IEvents? events,
        LocalPluginPeerRegistry? peers = null,
        IReadOnlyList<string>? peerTags = null)
        : base(events, peers, peerTags)
    {
    }

    /// <summary>
    /// Bind the graphical session's command adapter: it is both the typed
    /// command set and the chat-submission route.
    /// </summary>
    public void BindSessionCommands(CurrentGameRuntimeAdapter commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        BindSessionCommands(new SessionCommandSeam(
            commands,
            () => commands.Generation,
            commands.SubmitChatText));
    }
}
