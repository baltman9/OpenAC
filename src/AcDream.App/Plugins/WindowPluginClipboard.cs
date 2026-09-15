using AcDream.Plugin.Abstractions;
using Silk.NET.Input;

namespace AcDream.App.Plugins;

/// <summary>
/// The window's clipboard, reached through the same keyboard device the
/// retained text controls copy with. The keyboard is wired well after the
/// plugin host is built, so it is resolved on each call.
/// </summary>
public sealed class WindowPluginClipboard(Func<IKeyboard?> keyboard)
    : IPluginClipboard
{
    private readonly Func<IKeyboard?> _keyboard =
        keyboard ?? throw new ArgumentNullException(nameof(keyboard));

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
        try
        {
            device.ClipboardText = text;
            return true;
        }
        catch
        {
            // No window focus, or the platform refused the clipboard.
            return false;
        }
    }
}
