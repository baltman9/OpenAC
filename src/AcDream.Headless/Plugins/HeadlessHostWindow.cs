using AcDream.Plugin.Abstractions;

namespace AcDream.Headless.Plugins;

/// <summary>
/// A headless session has no OS window, so Minimize/Restore/IsMinimized
/// keep the interface's inert defaults. RequestClose still has somewhere
/// real to go: it ends the plugin's own session through the process's
/// normal terminal path -- the same one a SIGINT, a SIGTERM, or a
/// policy-driven stop already uses -- rather than terminating anything
/// directly.
/// </summary>
internal sealed class HeadlessHostWindow(Func<bool>? requestGracefulStop)
    : IHostWindow
{
    private readonly Func<bool>? _requestGracefulStop = requestGracefulStop;

    public HostWindowResult RequestClose() =>
        _requestGracefulStop is not null && _requestGracefulStop()
            ? new HostWindowResult(HostWindowStatus.Done)
            : new HostWindowResult(HostWindowStatus.Unavailable);
}
