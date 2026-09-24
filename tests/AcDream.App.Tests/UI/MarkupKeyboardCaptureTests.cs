using AcDream.App.UI;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.UI;

public sealed class MarkupKeyboardCaptureTests
{
    [Theory]
    [InlineData(12, 12)]
    [InlineData(12, 42)]
    [InlineData(12, 72)]
    public void ClickingMarkupActionKeepsGameBindingsAndAutomationAvailable(
        int x, int y)
    {
        const string xml = """
            <panel x="0" y="0" w="180" h="110">
              <button x="8" y="8" w="80" h="20" text="Start" onclick="{Activate}" />
              <tab x="8" y="38" w="80" h="20" text="Tab" selected="{Selected}" onclick="{Activate}" />
              <toggle x="8" y="68" w="80" h="20" text="Toggle" checked="{Selected}" onclick="{Activate}" />
            </panel>
            """;
        var binding = new BindingOwner();
        var panel = MarkupDocument.Build(xml, binding, _ => (1u, 32, 32));
        var root = new UiRoot { Width = 800, Height = 600 };
        root.AddChild(panel);
        var chat = new UiField { Left = 300, Width = 100, Height = 20 };
        root.AddChild(chat);
        root.DefaultTextInput = chat;
        var keyboard = new KeyboardSource();
        var mouse = new MouseSource(root);
        var bindings = new KeyBindings();
        bindings.Add(new Binding(new KeyChord(Key.W, ModifierMask.None),
            InputAction.MovementForward));
        bindings.Add(new Binding(new KeyChord(Key.Space, ModifierMask.None),
            InputAction.MovementJump));
        bindings.Add(new Binding(new KeyChord(Key.Enter, ModifierMask.None),
            InputAction.EnterChatMode));
        using var dispatcher = InputDispatcher.CreateDetached(keyboard, mouse, bindings);
        var fired = new List<InputAction>();
        dispatcher.Fired += (action, activation) =>
        {
            if (activation == ActivationType.Press)
                fired.Add(action);
        };
        dispatcher.Attach();

        root.OnMouseDown(UiMouseButton.Left, x, y);
        root.OnMouseUp(UiMouseButton.Left, x, y);

        Assert.Equal(1, binding.Activations);
        Assert.Null(root.KeyboardFocus);
        Assert.False(root.WantsKeyboard);
        keyboard.Press(Key.W);
        keyboard.Press(Key.Space);
        root.OnKeyDown((int)Key.Space);
        Assert.Equal(1, binding.Activations);
        Assert.True(dispatcher.TryInvokeAutomationAction(InputAction.CombatToggleCombat));
        Assert.True(dispatcher.TrySetAutomationActionHeld(InputAction.MovementForward, true));
        keyboard.Press(Key.Enter);
        root.OnKeyDown((int)Key.Enter);
        Assert.Same(chat, root.KeyboardFocus);
        Assert.Equal(1, binding.Activations);
        Assert.Equal([
            InputAction.MovementForward,
            InputAction.MovementJump,
            InputAction.CombatToggleCombat,
            InputAction.MovementForward,
            InputAction.EnterChatMode,
        ], fired);
    }

    private sealed class BindingOwner
    {
        public int Activations { get; private set; }
        public bool Selected => false;
        public Action Activate => () => Activations++;
    }

    private sealed class KeyboardSource : IKeyboardSource
    {
        public event Action<Key, ModifierMask>? KeyDown;
#pragma warning disable CS0067
        public event Action<Key, ModifierMask>? KeyUp;
#pragma warning restore CS0067
        public bool IsHeld(Key key) => false;
        public ModifierMask CurrentModifiers => ModifierMask.None;
        public void Press(Key key) => KeyDown?.Invoke(key, ModifierMask.None);
    }

    private sealed class MouseSource(UiRoot root) : IMouseSource
    {
#pragma warning disable CS0067
        public event Action<MouseButton, ModifierMask>? MouseDown;
        public event Action<MouseButton, ModifierMask>? MouseUp;
        public event Action<float, float>? MouseMove;
        public event Action<float>? Scroll;
#pragma warning restore CS0067
        public bool IsHeld(MouseButton button) => false;
        public bool WantCaptureMouse => root.WantsMouse;
        public bool WantCaptureKeyboard => root.WantsKeyboard;
    }
}
