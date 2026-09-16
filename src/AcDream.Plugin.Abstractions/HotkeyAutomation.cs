namespace AcDream.Plugin.Abstractions;

/// <summary>
/// A key a plugin can bind a hotkey to. Names mirror the graphical host's
/// own keyboard-input enum so a chord reads the same way to a plugin author
/// as it does in the client's own keybinds file, without this assembly
/// depending on that input layer.
/// </summary>
public enum PluginKey
{
    Unknown = 0,

    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,

    Number0, Number1, Number2, Number3, Number4,
    Number5, Number6, Number7, Number8, Number9,

    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,

    Space,
    Enter,
    Escape,
    Tab,
    Backspace,
    Delete,
    Insert,
    Home,
    End,
    PageUp,
    PageDown,
    Up,
    Down,
    Left,
    Right,

    Minus,
    Equal,
    LeftBracket,
    RightBracket,
    BackSlash,
    Semicolon,
    Apostrophe,
    Comma,
    Period,
    Slash,
}

/// <summary>
/// A key plus the modifiers required to trigger it. Ctrl/Alt/Shift are
/// side-independent: either physical key of a held pair satisfies the
/// chord.
/// </summary>
public readonly record struct PluginKeyChord(
    PluginKey Key,
    bool Ctrl = false,
    bool Alt = false,
    bool Shift = false);

/// <summary>A live hotkey registration returned by <see cref="IHotkeyRegistry.Register"/>.</summary>
public interface IPluginHotkeyRegistration : IDisposable
{
    /// <summary>
    /// False when the requested chord collided with an already-bound client
    /// action or another plugin's hotkey and the registration was refused;
    /// the handler will never fire.
    /// </summary>
    bool IsBound { get; }

    /// <summary>
    /// The chord actually bound: the caller's default unless a stored user
    /// override replaced it at registration time.
    /// </summary>
    PluginKeyChord EffectiveChord { get; }

    /// <summary>
    /// Rebinds this registration to a new chord, persisting it as a user
    /// override under this hotkey's scoped id so it survives across
    /// sessions. Re-resolves immediately if the host's keyboard/dispatcher
    /// are already up; a host with nothing to bind to (headless) treats
    /// this as a no-op and IsBound stays false.
    /// </summary>
    void Rebind(PluginKeyChord chord);
}

/// <summary>A registration handle from a host with nothing to bind (headless).</summary>
public sealed class NoOpHotkeyRegistration : IPluginHotkeyRegistration
{
    public static NoOpHotkeyRegistration Instance { get; } = new();

    private NoOpHotkeyRegistration()
    {
    }

    public bool IsBound => false;
    public PluginKeyChord EffectiveChord => default;
    public void Rebind(PluginKeyChord chord) { }

    public void Dispose()
    {
    }
}

public interface IHotkeyRegistry
{
    /// <summary>
    /// Registers a plugin-owned hotkey. <paramref name="id"/> identifies the
    /// binding for persistence (scoped by plugin, so two plugins may each
    /// use the same id); <paramref name="displayName"/> is shown in the
    /// rebind UI. A stored user override for this id replaces
    /// <paramref name="defaultChord"/> when present. The handler does not
    /// fire while the chat bar has keyboard focus unless the chord includes
    /// Ctrl or Alt. Disposing the result revokes the binding; the host also
    /// revokes every hotkey a plugin registered when that plugin unloads.
    /// </summary>
    IPluginHotkeyRegistration Register(
        string id,
        string displayName,
        PluginKeyChord defaultChord,
        Action handler) =>
        NoOpHotkeyRegistration.Instance;
}

public sealed class NoOpHotkeyRegistry : IHotkeyRegistry
{
    public static NoOpHotkeyRegistry Instance { get; } = new();

    private NoOpHotkeyRegistry()
    {
    }

    public IPluginHotkeyRegistration Register(
        string id,
        string displayName,
        PluginKeyChord defaultChord,
        Action handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(handler);
        return NoOpHotkeyRegistration.Instance;
    }
}
