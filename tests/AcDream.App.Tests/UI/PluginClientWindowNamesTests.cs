using AcDream.App.UI;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Tests.UI;

public sealed class PluginClientWindowNamesTests
{
    [Theory]
    [InlineData(PluginClientWindow.Inventory, "inventory")]
    [InlineData(PluginClientWindow.Character, "character")]
    [InlineData(PluginClientWindow.CharacterInformation, "character-information")]
    [InlineData(PluginClientWindow.Spellbook, "spellbook")]
    [InlineData(PluginClientWindow.Map, "map-house")]
    [InlineData(PluginClientWindow.Options, "options")]
    [InlineData(PluginClientWindow.Social, "social-panel")]
    [InlineData(PluginClientWindow.Journal, "journal")]
    [InlineData(PluginClientWindow.PositiveEffects, "effects-positive")]
    [InlineData(PluginClientWindow.NegativeEffects, "effects-negative")]
    [InlineData(PluginClientWindow.LinkStatus, "link-status")]
    [InlineData(PluginClientWindow.Vitae, "vitae")]
    [InlineData(PluginClientWindow.Radar, "radar")]
    [InlineData(PluginClientWindow.KeyboardConfig, "keyboard-config")]
    public void EveryEnumMemberMapsToItsRetainedWindowName(
        PluginClientWindow window, string expectedName)
    {
        Assert.True(PluginClientWindowNames.TryGetName(window, out string name));
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void EveryDefinedEnumValueResolves()
    {
        // Guards against a member added to PluginClientWindow without a
        // matching row in the switch: an un-mapped member would silently
        // report "unavailable" to every plugin forever.
        foreach (PluginClientWindow window in Enum.GetValues<PluginClientWindow>())
        {
            Assert.True(
                PluginClientWindowNames.TryGetName(window, out string name),
                $"{window} has no WindowNames mapping.");
            Assert.False(string.IsNullOrEmpty(name));
        }
    }

    [Fact]
    public void UndefinedEnumValueIsUnavailable()
    {
        Assert.False(PluginClientWindowNames.TryGetName((PluginClientWindow)(-1), out string name));
        Assert.Equal(string.Empty, name);
    }
}
