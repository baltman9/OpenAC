using AcDream.App.Plugins;
using Silk.NET.Windowing;

namespace AcDream.App.Tests.Plugins;

/// <summary>
/// A minimal stand-in for the native window, used only to exercise
/// WindowPluginHostWindow. Deliberately implements the narrow
/// IPluginHostWindowTarget seam rather than Silk.NET's much larger
/// IWindow, exactly why that seam exists.
/// </summary>
internal sealed class FakeHostWindowTarget : IPluginHostWindowTarget
{
    private WindowState _state = WindowState.Normal;

    /// <summary>
    /// When set, models a write that does not stick (a platform that
    /// refuses the state change, or no window focus) -- the setter runs,
    /// but the backing state does not follow.
    /// </summary>
    public Func<WindowState, bool>? OnSetWindowState { get; set; }

    public int CloseCount { get; private set; }

    public WindowState WindowState
    {
        get => _state;
        set
        {
            if (OnSetWindowState is null || OnSetWindowState(value))
                _state = value;
        }
    }

    public void Close() => CloseCount++;
}
