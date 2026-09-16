namespace AcDream.Plugin.Abstractions;

/// <summary>
/// One of the client's own retained windows that a plugin may show, hide,
/// toggle, or query -- the same windows a player opens with a keybind or a
/// toolbar button. A no-window host, or a window this build does not mount,
/// reports every operation as unavailable (<c>false</c>).
/// </summary>
public enum PluginClientWindow
{
    Inventory,
    Character,
    CharacterInformation,
    Spellbook,
    Map,
    Options,
    Social,
    Journal,
    PositiveEffects,
    NegativeEffects,
    LinkStatus,
    Vitae,
    Radar,
}
