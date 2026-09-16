namespace AcDream.App.Tests.Plugins;

#pragma warning disable CS0067 // Events required by IKeyboard, unused by these tests.

internal sealed class FakeKeyboard : Silk.NET.Input.IKeyboard
{
    public string ClipboardText { get; set; } = string.Empty;
    public Func<string, bool>? OnSetClipboardText { get; set; }

    string Silk.NET.Input.IInputDevice.Name => "fake-keyboard";
    int Silk.NET.Input.IInputDevice.Index => 0;
    bool Silk.NET.Input.IInputDevice.IsConnected => true;
    IReadOnlyList<Silk.NET.Input.Key> Silk.NET.Input.IKeyboard.SupportedKeys => [];

    string Silk.NET.Input.IKeyboard.ClipboardText
    {
        get => ClipboardText;
        set
        {
            // OnSetClipboardText models the silent-failure case: the write
            // is "accepted" (no exception) but does not actually change
            // what a later read reports -- exactly what an off-main-thread
            // GLFW clipboard call does.
            if (OnSetClipboardText is null || OnSetClipboardText(value))
                ClipboardText = value;
        }
    }

    public bool IsKeyPressed(Silk.NET.Input.Key key) => false;
    public bool IsScancodePressed(int scancode) => false;
    public void BeginInput() { }
    public void EndInput() { }

    public event Action<Silk.NET.Input.IKeyboard, Silk.NET.Input.Key, int>? KeyDown;
    public event Action<Silk.NET.Input.IKeyboard, Silk.NET.Input.Key, int>? KeyUp;
    public event Action<Silk.NET.Input.IKeyboard, char>? KeyChar;
}
#pragma warning restore CS0067
