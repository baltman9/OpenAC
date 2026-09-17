using AcDream.Plugin.Abstractions;
using Silk.NET.Windowing;

namespace AcDream.App.Plugins;

/// <summary>
/// Minimize/restore/close for the OS window that hosts the client, reached
/// through the same window the close button and the clipboard already use
/// (via the narrow <see cref="IPluginHostWindowTarget"/> seam, not the full
/// Silk.NET window surface). A plugin call can arrive on any thread; every
/// write is marshalled onto the window's own thread through
/// <see cref="MainThreadDispatchQueue"/> -- GLFW window-state and close
/// calls are documented main-thread-only, the same hazard the clipboard
/// write already guards against.
/// </summary>
public sealed class WindowPluginHostWindow(
    Func<IPluginHostWindowTarget?> window,
    Func<bool> isMinimized,
    Func<MainThreadDispatchQueue> dispatch)
    : IHostWindow
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(1);

    private readonly Func<IPluginHostWindowTarget?> _window =
        window ?? throw new ArgumentNullException(nameof(window));
    private readonly Func<bool> _isMinimized =
        isMinimized ?? throw new ArgumentNullException(nameof(isMinimized));
    private readonly Func<MainThreadDispatchQueue> _dispatch =
        dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    /// <summary>
    /// Reads the cached minimized flag rather than the window's own
    /// WindowState -- that read is itself a main-thread-only GLFW call, so
    /// the host keeps a copy current via the window's StateChanged event
    /// instead of touching Silk.NET from whatever thread a plugin calls
    /// this from.
    /// </summary>
    public bool IsMinimized => _isMinimized();

    public HostWindowResult Minimize() => SetState(WindowState.Minimized);

    public HostWindowResult Restore() => SetState(WindowState.Normal);

    public HostWindowResult RequestClose()
    {
        IPluginHostWindowTarget? target = Resolve();
        if (target is null)
            return new HostWindowResult(HostWindowStatus.Unavailable);

        // Close() is the exact route the window's own close button and
        // OS-close-request handling both use: it raises the Closing
        // callback, which runs the graceful logout and teardown before the
        // process exits. This never calls Environment.Exit or Process.Kill
        // directly.
        bool reached = _dispatch().InvokeAndWait(target.Close, CallTimeout);
        return reached
            ? new HostWindowResult(HostWindowStatus.Done)
            : new HostWindowResult(HostWindowStatus.Unavailable);
    }

    private HostWindowResult SetState(WindowState desired)
    {
        IPluginHostWindowTarget? target = Resolve();
        if (target is null)
            return new HostWindowResult(HostWindowStatus.Unavailable);

        bool changed = false;
        void Apply()
        {
            target.WindowState = desired;
            // The write can silently fail to land (minimized-by-policy,
            // no window focus, a platform that refuses the state) --
            // read back what actually stuck instead of trusting the
            // setter, the same rule WindowPluginClipboard applies to its
            // own write.
            changed = target.WindowState == desired;
        }

        bool reached = _dispatch().InvokeAndWait(Apply, CallTimeout);
        return reached && changed
            ? new HostWindowResult(HostWindowStatus.Done)
            : new HostWindowResult(HostWindowStatus.Unavailable);
    }

    private IPluginHostWindowTarget? Resolve()
    {
        try
        {
            return _window();
        }
        catch
        {
            return null;
        }
    }
}
