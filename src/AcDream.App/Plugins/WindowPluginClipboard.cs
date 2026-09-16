using AcDream.Plugin.Abstractions;
using Silk.NET.Input;

namespace AcDream.App.Plugins;

/// <summary>
/// The window's clipboard, reached through the same keyboard device the
/// retained text controls copy with. The keyboard is wired well after the
/// plugin host is built, so it is resolved on each call.
/// </summary>
public sealed class WindowPluginClipboard(
    Func<IKeyboard?> keyboard,
    Func<MainThreadDispatchQueue?> dispatch)
    : IPluginClipboard
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(1);

    private readonly Func<IKeyboard?> _keyboard =
        keyboard ?? throw new ArgumentNullException(nameof(keyboard));
    private readonly Func<MainThreadDispatchQueue?> _dispatch =
        dispatch ?? throw new ArgumentNullException(nameof(dispatch));

    public bool TrySetText(string text)
    {
        if (text is null)
            return false;
        IKeyboard? device;
        try
        {
            device = _keyboard();
        }
        catch
        {
            return false;
        }
        if (device is null)
            return false;

        bool written = false;
        void Write()
        {
            try
            {
                device.ClipboardText = text;
                // The underlying GLFW clipboard write can fail silently --
                // no exception, no falsy return -- when the platform
                // refuses it (or when it ran off the window's own thread,
                // which the dispatch queue below exists to prevent). Read
                // back what actually landed instead of trusting the
                // setter.
                written = device.ClipboardText == text;
            }
            catch
            {
                // No window focus, or the platform refused the clipboard.
                written = false;
            }
        }

        MainThreadDispatchQueue? queue = _dispatch();
        if (queue is null)
        {
            // No dispatch queue wired yet (very early startup). GLFW
            // clipboard calls are main-thread-only, so this inline path is
            // only correct if the caller already is on that thread.
            Write();
            return written;
        }
        return queue.InvokeAndWait(Write, WriteTimeout) && written;
    }
}
