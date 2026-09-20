using System.Runtime.CompilerServices;
using AcDream.App.Configuration;
using AcDream.Headless.Configuration;
using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

/// <summary>
/// One session-config document, read by both clients' readers. A player who
/// writes a session file should not have to know which client will read it:
/// every field that decides what a plugin sees has to be accepted by both and
/// mean the same thing on both. A field only one client can act on is
/// accepted and ignored by the other rather than refused, so the same file
/// still starts either client.
/// </summary>
public sealed class SessionConfigPluginFieldParityTests
{
    [Fact]
    public void BothReadersAcceptTheSameDocumentAndAgreeOnEveryPluginFacingField()
    {
        string path = SharedFixturePath();

        (_, SessionDescriptor windowed) = SessionConfigurationLoader.Load(path);
        HeadlessSessionDescriptor windowless = Assert.Single(
            HeadlessConfigurationLoader.Load(path).Sessions)!;

        Assert.Equal(windowless.Id, windowed.Id);
        Assert.Equal(windowless.Account, windowed.Account);
        Assert.Equal(windowless.Character?.Index, windowed.Character?.Index);
        Assert.Equal(windowless.Character?.Id, windowed.Character?.Id);
        Assert.Equal(windowless.Character?.Name, windowed.Character?.Name);
        Assert.Equal(windowless.CharacterOptions, windowed.CharacterOptions);
        Assert.Equal(windowless.Plugins, windowed.Plugins);
        Assert.Equal(windowless.PluginTags, windowed.PluginTags);
        Assert.Equal(windowless.LoginCommands, windowed.LoginCommands);
        Assert.Equal(
            windowless.LoginCommandDelayMs,
            windowed.LoginCommandDelayMs);
        Assert.Equal(windowless.StatusFile, windowed.StatusFile);
        AssertSamePluginSettings(
            windowless.PluginSettings,
            windowed.PluginSettings);
    }

    [Fact]
    public void TheDocumentReallyCarriesEveryPluginFacingField()
    {
        (_, SessionDescriptor windowed) =
            SessionConfigurationLoader.Load(SharedFixturePath());

        Assert.Equal(["ExamplePlugin", "AnotherPlugin"], windowed.Plugins);
        Assert.Equal(
            ["shared-fixture-group", "second-word"],
            windowed.PluginTags);
        Assert.Equal(
            ["/tell someone, hi", "/vt start"],
            windowed.LoginCommands);
        Assert.Equal(750, windowed.LoginCommandDelayMs);
        Assert.NotNull(windowed.CharacterOptions);
        Assert.True(windowed.CharacterOptions!["UseChargeAttack"]);
        Assert.Equal(
            "escort",
            windowed.PluginSettings!["ExamplePlugin"]["mode"]);
    }

    [Fact]
    public void TheWindowedClientAnswersPluginsFromTheDocumentsSettingsAndTags()
    {
        (SessionConfiguration configuration, SessionDescriptor session) =
            SessionConfigurationLoader.Load(SharedFixturePath());

        RuntimeOptionsView view = ReadWindowedOptions(
            configuration,
            session,
            _ => null);

        Assert.Equal(
            ["shared-fixture-group", "second-word"],
            view.PluginTags);
        Assert.Equal(
            "shared",
            view.PluginSettings!.SessionSettingsFor("ExamplePlugin")["profile"]);
    }

    [Fact]
    public void TheDocumentOutranksTheStartupOptionsForTagsAndSettings()
    {
        (SessionConfiguration configuration, SessionDescriptor session) =
            SessionConfigurationLoader.Load(SharedFixturePath());

        RuntimeOptionsView view = ReadWindowedOptions(
            configuration,
            session,
            name => name switch
            {
                "ACDREAM_PLUGIN_TAGS" => "an-option-word",
                "ACDREAM_PLUGIN_SETTINGS_FILE" => "no-such-file.json",
                _ => null,
            });

        Assert.Equal(
            ["shared-fixture-group", "second-word"],
            view.PluginTags);
        Assert.Equal(
            "shared",
            view.PluginSettings!.SessionSettingsFor("ExamplePlugin")["profile"]);
    }

    private sealed record RuntimeOptionsView(
        IReadOnlyList<string> PluginTags,
        PluginSessionSettings? PluginSettings);

    private static RuntimeOptionsView ReadWindowedOptions(
        SessionConfiguration configuration,
        SessionDescriptor session,
        Func<string, string?> environment)
    {
        AcDream.App.RuntimeOptions options =
            AcDream.App.RuntimeOptions.FromSessionConfig(
                "dats",
                environment,
                SharedFixturePath(),
                configuration,
                session,
                resolvedPassword: "password");
        return new RuntimeOptionsView(
            options.PluginTags,
            options.SessionPluginSettings);
    }

    private static void AssertSamePluginSettings(
        IReadOnlyDictionary<string, Dictionary<string, string>>? windowless,
        IReadOnlyDictionary<string, Dictionary<string, string>>? windowed)
    {
        if (windowless is null || windowed is null)
        {
            Assert.Equal(windowless is null, windowed is null);
            return;
        }

        Assert.Equal(
            windowless.Keys.OrderBy(static key => key, StringComparer.Ordinal),
            windowed.Keys.OrderBy(static key => key, StringComparer.Ordinal));
        foreach ((string pluginId, Dictionary<string, string> settings)
            in windowless)
        {
            Assert.Equal(settings, windowed[pluginId]);
        }
    }

    private static string SharedFixturePath(
        [CallerFilePath] string sourcePath = "") =>
        Path.Combine(
            FindRepositoryRoot(sourcePath),
            "tests",
            "Fixtures",
            "launcher-session",
            "session-config-shared-fixture.json");

    private static string FindRepositoryRoot(string sourcePath)
    {
        string[] starts =
        [
            Path.GetDirectoryName(sourcePath) ?? string.Empty,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        ];
        foreach (string start in starts)
        {
            if (string.IsNullOrEmpty(start))
                continue;

            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find AcDream.slnx above the working or output directory.");
    }
}
