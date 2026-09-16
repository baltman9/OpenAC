using AcDream.App.Input;
using AcDream.Plugin.Abstractions;
using AcDream.UI.Abstractions.Input;
using Silk.NET.Input;

namespace AcDream.App.Tests.Input;

public sealed class AppHotkeyRegistryTests
{
    [Fact]
    public void ARegistrationBeforeBindIsResolvedOnceBindRuns()
    {
        var registry = new AppHotkeyRegistry(overridesFilePath: null);
        int fired = 0;
        IPluginHotkeyRegistration handle = registry.Register(
            "test", "Test", new PluginKeyChord(PluginKey.F9), () => fired++);

        // Not bound yet: the dispatcher/keyboard do not exist.
        Assert.False(handle.IsBound);

        var keyboard = new FakeKeyboard();
        var bindings = new KeyBindings();
        InputDispatcher dispatcher = InputDispatcher.CreateDetached(
            keyboard, new FakeMouse(), bindings);
        registry.Bind(keyboard, bindings, dispatcher);

        Assert.True(handle.IsBound);

        keyboard.Fire(Key.F9, ModifierMask.None);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void ARegistrationCollidingWithAClientBindingIsRefused()
    {
        var registry = new AppHotkeyRegistry(overridesFilePath: null);
        var keyboard = new FakeKeyboard();
        KeyBindings bindings = KeyBindings.RetailDefaults();
        InputDispatcher dispatcher = InputDispatcher.CreateDetached(
            keyboard, new FakeMouse(), bindings);
        registry.Bind(keyboard, bindings, dispatcher);

        // Escape with no modifiers is a real retail default binding
        // (EscapeKey); a plugin asking for the exact same chord must be
        // refused rather than silently stealing the client's own key.
        int fired = 0;
        IPluginHotkeyRegistration handle = registry.Register(
            "colliding", "Colliding", new PluginKeyChord(PluginKey.Escape), () => fired++);

        Assert.False(handle.IsBound);

        keyboard.Fire(Key.Escape, ModifierMask.None);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void DisposingTheRegistrationStopsTheHandlerFromFiring()
    {
        var registry = new AppHotkeyRegistry(overridesFilePath: null);
        var keyboard = new FakeKeyboard();
        KeyBindings bindings = new KeyBindings();
        InputDispatcher dispatcher = InputDispatcher.CreateDetached(
            keyboard, new FakeMouse(), bindings);
        registry.Bind(keyboard, bindings, dispatcher);

        int fired = 0;
        IPluginHotkeyRegistration handle = registry.Register(
            "test", "Test", new PluginKeyChord(PluginKey.F9), () => fired++);
        handle.Dispose();

        keyboard.Fire(Key.F9, ModifierMask.None);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void ANonCtrlAltChordDoesNotFireWhileTheChatBarHasFocus()
    {
        var registry = new AppHotkeyRegistry(overridesFilePath: null);
        var keyboard = new FakeKeyboard();
        KeyBindings bindings = new KeyBindings();
        InputDispatcher dispatcher = InputDispatcher.CreateDetached(
            keyboard, new FakeMouse(), bindings);
        dispatcher.PushScope(InputScope.Chat);
        registry.Bind(keyboard, bindings, dispatcher);

        int plainFired = 0;
        int ctrlFired = 0;
        registry.Register(
            "plain", "Plain", new PluginKeyChord(PluginKey.F9), () => plainFired++);
        registry.Register(
            "ctrl", "Ctrl", new PluginKeyChord(PluginKey.F10, Ctrl: true), () => ctrlFired++);

        keyboard.Fire(Key.F9, ModifierMask.None);
        keyboard.Fire(Key.F10, ModifierMask.Ctrl);

        Assert.Equal(0, plainFired);
        Assert.Equal(1, ctrlFired);
    }


    [Fact]
    public void ASecondRegistrationForTheSameIdReplacesTheFirst()
    {
        var registry = new AppHotkeyRegistry(overridesFilePath: null);
        var keyboard = new FakeKeyboard();
        var bindings = new KeyBindings();
        InputDispatcher dispatcher = InputDispatcher.CreateDetached(
            keyboard, new FakeMouse(), bindings);
        registry.Bind(keyboard, bindings, dispatcher);

        int firstFired = 0;
        int secondFired = 0;
        IPluginHotkeyRegistration first = registry.Register(
            "same-id", "First", new PluginKeyChord(PluginKey.F9), () => firstFired++);
        Assert.True(first.IsBound);

        IPluginHotkeyRegistration second = registry.Register(
            "same-id", "Second", new PluginKeyChord(PluginKey.F10), () => secondFired++);

        // The first registration is dead -- both its own chord and the
        // fact that it no longer fires prove it was replaced, not merely
        // shadowed.
        Assert.False(first.IsBound);
        Assert.True(second.IsBound);

        keyboard.Fire(Key.F9, ModifierMask.None);
        keyboard.Fire(Key.F10, ModifierMask.None);
        Assert.Equal(0, firstFired);
        Assert.Equal(1, secondFired);
    }

    [Fact]
    public void ARegistrationCollidingWithAnotherPluginsChordIsRefused()
    {
        var registry = new AppHotkeyRegistry(overridesFilePath: null);
        var keyboard = new FakeKeyboard();
        var bindings = new KeyBindings();
        InputDispatcher dispatcher = InputDispatcher.CreateDetached(
            keyboard, new FakeMouse(), bindings);
        registry.Bind(keyboard, bindings, dispatcher);

        int firstFired = 0;
        int secondFired = 0;
        IPluginHotkeyRegistration first = registry.Register(
            "plugin-a/heal", "Heal", new PluginKeyChord(PluginKey.H, Ctrl: true), () => firstFired++);
        IPluginHotkeyRegistration second = registry.Register(
            "plugin-b/heal", "Heal", new PluginKeyChord(PluginKey.H, Ctrl: true), () => secondFired++);

        Assert.True(first.IsBound);
        Assert.False(second.IsBound);

        keyboard.Fire(Key.H, ModifierMask.Ctrl);
        Assert.Equal(1, firstFired);
        Assert.Equal(0, secondFired);

        // Freeing the first registration must let a fresh request for the
        // same chord bind -- the collision is against LIVE entries only.
        first.Dispose();
        IPluginHotkeyRegistration third = registry.Register(
            "plugin-c/heal", "Heal", new PluginKeyChord(PluginKey.H, Ctrl: true), () => { });
        Assert.True(third.IsBound);
    }

    [Fact]
    public void RebindPersistsAcrossARegistryRestart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"acdream-plugin-hotkeys-{Guid.NewGuid():N}.json");
        try
        {
            var registry = new AppHotkeyRegistry(overridesFilePath: path);
            var keyboard = new FakeKeyboard();
            var bindings = new KeyBindings();
            InputDispatcher dispatcher = InputDispatcher.CreateDetached(
                keyboard, new FakeMouse(), bindings);
            registry.Bind(keyboard, bindings, dispatcher);

            int fired = 0;
            IPluginHotkeyRegistration handle = registry.Register(
                "quick-heal", "Quick Heal", new PluginKeyChord(PluginKey.H, Ctrl: true), () => fired++);
            Assert.Equal(new PluginKeyChord(PluginKey.H, Ctrl: true), handle.EffectiveChord);

            handle.Rebind(new PluginKeyChord(PluginKey.J, Ctrl: true));
            Assert.Equal(new PluginKeyChord(PluginKey.J, Ctrl: true), handle.EffectiveChord);
            keyboard.Fire(Key.J, ModifierMask.Ctrl);
            Assert.Equal(1, fired);

            // A fresh registry instance (simulating a relaunch) picks up the
            // stored override at Register time, not the caller's default.
            var restarted = new AppHotkeyRegistry(overridesFilePath: path);
            var keyboard2 = new FakeKeyboard();
            var bindings2 = new KeyBindings();
            InputDispatcher dispatcher2 = InputDispatcher.CreateDetached(
                keyboard2, new FakeMouse(), bindings2);
            restarted.Bind(keyboard2, bindings2, dispatcher2);
            IPluginHotkeyRegistration reloaded = restarted.Register(
                "quick-heal", "Quick Heal", new PluginKeyChord(PluginKey.H, Ctrl: true), () => { });
            Assert.Equal(new PluginKeyChord(PluginKey.J, Ctrl: true), reloaded.EffectiveChord);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void BindIsIdempotentAndDoesNotDoubleFireAnExistingRegistration()
    {
        var registry = new AppHotkeyRegistry(overridesFilePath: null);
        var keyboard = new FakeKeyboard();
        var bindings = new KeyBindings();
        InputDispatcher dispatcher = InputDispatcher.CreateDetached(
            keyboard, new FakeMouse(), bindings);

        int fired = 0;
        registry.Register(
            "test", "Test", new PluginKeyChord(PluginKey.F9), () => fired++);

        registry.Bind(keyboard, bindings, dispatcher);
        registry.Bind(keyboard, bindings, dispatcher);

        keyboard.Fire(Key.F9, ModifierMask.None);
        Assert.Equal(1, fired);
    }

    private sealed class FakeKeyboard : IKeyboardSource
    {
        public event Action<Key, ModifierMask>? KeyDown;
#pragma warning disable CS0067
        public event Action<Key, ModifierMask>? KeyUp;
#pragma warning restore CS0067
        public bool IsHeld(Key key) => false;
        public ModifierMask CurrentModifiers => ModifierMask.None;

        public void Fire(Key key, ModifierMask modifiers) =>
            KeyDown?.Invoke(key, modifiers);
    }

    private sealed class FakeMouse : IMouseSource
    {
#pragma warning disable CS0067
        public event Action<MouseButton, ModifierMask>? MouseDown;
        public event Action<MouseButton, ModifierMask>? MouseUp;
        public event Action<float, float>? MouseMove;
        public event Action<float>? Scroll;
#pragma warning restore CS0067
        public bool WantCaptureKeyboard { get; set; }
        public bool WantCaptureMouse { get; set; }
        public bool IsHeld(MouseButton button) => false;
    }
}
