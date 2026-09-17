namespace AcDream.Plugin.Abstractions;

/// <summary>Whether a host-window operation actually happened.</summary>
public enum HostWindowStatus
{
    /// <summary>
    /// The host has no window to act on (a no-window/headless host), or the
    /// requested change did not take.
    /// </summary>
    Unavailable = 0,

    /// <summary>The operation reached the host and took effect.</summary>
    Done,
}

/// <summary>One outcome from an <see cref="IHostWindow"/> call.</summary>
public readonly record struct HostWindowResult(
    HostWindowStatus Status,
    string? Notice = null)
{
    public bool Succeeded => Status == HostWindowStatus.Done;
}

/// <summary>
/// The client's own OS window -- minimize, restore, and close, the same
/// three controls the window's title bar already offers. A host with no
/// window (a headless bot process) answers every query as unavailable
/// rather than throwing, so a plugin written against a graphical host still
/// loads there.
/// </summary>
public interface IHostWindow
{
    /// <summary>
    /// Whether the window is currently minimized/iconified. Always
    /// <c>false</c> on a host with no window.
    /// </summary>
    bool IsMinimized => false;

    /// <summary>
    /// Minimizes the OS window (GLFW iconify; Windows, Linux, macOS).
    /// Reports <see cref="HostWindowStatus.Done"/> only once the window
    /// actually reports the minimized state back; a host with no window,
    /// or one that refused the change, reports <see
    /// cref="HostWindowStatus.Unavailable"/>.
    /// </summary>
    HostWindowResult Minimize() => new(HostWindowStatus.Unavailable);

    /// <summary>
    /// Restores the OS window to its normal (non-minimized) state. Same
    /// success rule as <see cref="Minimize"/>.
    /// </summary>
    HostWindowResult Restore() => new(HostWindowStatus.Unavailable);

    /// <summary>
    /// Requests that the client close, following the exact route its own
    /// close button uses -- graceful logout, then teardown, then process
    /// exit. This never terminates the process directly: a host with no
    /// window ends the plugin's own session through its normal terminal
    /// path (the same one a SIGINT or a policy-driven stop already uses)
    /// instead.
    /// </summary>
    HostWindowResult RequestClose() => new(HostWindowStatus.Unavailable);
}

/// <summary>Shared inert window for hosts with nothing to act on.</summary>
public sealed class NoOpHostWindow : IHostWindow
{
    public static NoOpHostWindow Instance { get; } = new();

    private NoOpHostWindow()
    {
    }
}
