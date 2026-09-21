using AcDream.Headless.Configuration;
using AcDream.Runtime.Plugins;

namespace AcDream.Headless.Tests;

/// <summary>
/// Where a windowless session's startup settings for plugins come from when
/// the session document names none.
/// </summary>
/// <remarks>
/// The startup option naming a file used to be read by the client with a
/// window and nowhere else, while being documented as the option both clients
/// share. A plugin started from that file behaved one way under a window and
/// another way without one, which is the single thing these settings exist to
/// prevent.
/// </remarks>
public sealed class HeadlessPluginSettingsFileTests
{
    [Fact]
    public void ASessionThatNamesNoSettingsReadsTheFileTheOptionNames()
    {
        using var file = new SettingsFile(
            """
            { "acdream.example": { "profile": "tank" } }
            """);

        PluginSessionSettings settings = HeadlessPluginSettingsOptions.Resolve(
            declared: null,
            Env(file.Path));

        Assert.Equal(
            "tank",
            settings.SessionSettingsFor("acdream.example")["profile"]);
    }

    [Fact]
    public void WhatTheSessionNamesOutranksTheFileAndLeavesItUnread()
    {
        PluginSessionSettings settings = HeadlessPluginSettingsOptions.Resolve(
            new Dictionary<string, Dictionary<string, string>>
            {
                ["acdream.example"] = new() { ["profile"] = "from the session" },
            },
            Env("no-such-file.json"));

        Assert.Equal(
            "from the session",
            settings.SessionSettingsFor("acdream.example")["profile"]);
    }

    [Fact]
    public void NoSessionSettingsAndNoFileMeansNoSettings()
    {
        Assert.Same(
            PluginSessionSettings.Empty,
            HeadlessPluginSettingsOptions.Resolve(
                declared: null,
                Env(null)));
        Assert.Same(
            PluginSessionSettings.Empty,
            HeadlessPluginSettingsOptions.Resolve(
                declared: null,
                Env("   ")));
    }

    [Fact]
    public void AFileThatCannotBeReadStopsTheSessionAndSaysWhy()
    {
        Assert.ThrowsAny<Exception>(() =>
            HeadlessPluginSettingsOptions.Resolve(
                declared: null,
                Env("no-such-file.json")));
    }

    [Fact]
    public void AFileOfTheWrongShapeIsRefusedInTheSameWordsAsASessionsOwn()
    {
        using var file = new SettingsFile(
            """
            { "acdream.example": { "profile": null } }
            """);

        PluginSessionSettingsException fromFile =
            Assert.Throws<PluginSessionSettingsException>(() =>
                HeadlessPluginSettingsOptions.Resolve(
                    declared: null,
                    Env(file.Path)));
        PluginSessionSettingsException fromSession =
            Assert.Throws<PluginSessionSettingsException>(() =>
                HeadlessPluginSettingsOptions.Resolve(
                    new Dictionary<string, Dictionary<string, string>>
                    {
                        ["acdream.example"] = new() { ["profile"] = null! },
                    },
                    Env(null)));

        // The file names itself first and then says the same thing about
        // the same setting, in the same words.
        Assert.Contains(fromSession.Message, fromFile.Message, StringComparison.Ordinal);
    }

    private static Func<string, string?> Env(string? path) =>
        name => name == HeadlessPluginSettingsOptions.EnvironmentVariable
            ? path
            : null;

    private sealed class SettingsFile : IDisposable
    {
        internal SettingsFile(string contents)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-plugin-settings-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, contents);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
            }
        }
    }
}
