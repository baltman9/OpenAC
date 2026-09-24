namespace AcDream.App.UI;

internal sealed class UiKeyboardActivation
{
    public bool Focused { get; private set; }

    public bool HandleEvent(in UiEvent e, bool tabStop, bool enabled, Action? activate)
    {
        if (!tabStop)
            return false;

        if (e.Type == UiEventType.FocusGained)
        {
            Focused = true;
            return true;
        }

        if (e.Type == UiEventType.FocusLost)
        {
            Focused = false;
            return true;
        }

        if (e.Type == UiEventType.KeyDown && enabled
            && ((Silk.NET.Input.Key)e.Data0 is Silk.NET.Input.Key.Enter
                or Silk.NET.Input.Key.KeypadEnter or Silk.NET.Input.Key.Space))
        {
            activate?.Invoke();
            return true;
        }

        return false;
    }
}
