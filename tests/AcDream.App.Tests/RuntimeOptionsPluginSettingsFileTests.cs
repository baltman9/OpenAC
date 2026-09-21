using System.Collections.Generic;
using AcDream.App;

namespace AcDream.App.Tests;

/// <summary>
/// The launch option that gives the windowed client the same per-plugin
/// startup settings a session file gives the client with no window. It enters
/// through the options record like every other startup option, so nothing
/// reads the environment behind its back.
/// </summary>
public sealed class RuntimeOptionsPluginSettingsFileTests
{
    private const string AnyDatDir = "D:\\dat";

    [Fact]
    public void UnsetMeansNoFileAndSoNoSettings()
    {
        RuntimeOptions options = RuntimeOptions.Parse(AnyDatDir, _ => null);

        Assert.Null(options.PluginSettingsFile);
    }

    [Fact]
    public void AnEmptyValueIsReadAsUnset()
    {
        RuntimeOptions options = RuntimeOptions.Parse(
            AnyDatDir,
            Env(new() { ["ACDREAM_PLUGIN_SETTINGS_FILE"] = string.Empty }));

        Assert.Null(options.PluginSettingsFile);
    }

    [Fact]
    public void TheNamedPathIsCarriedOnTheOptionsRecord()
    {
        RuntimeOptions options = RuntimeOptions.Parse(
            AnyDatDir,
            Env(new()
            {
                ["ACDREAM_PLUGIN_SETTINGS_FILE"] = "D:\\bots\\plugin-settings.json",
            }));

        Assert.Equal("D:\\bots\\plugin-settings.json", options.PluginSettingsFile);
    }

    private static Func<string, string?> Env(Dictionary<string, string?> values) =>
        name => values.GetValueOrDefault(name);
}
