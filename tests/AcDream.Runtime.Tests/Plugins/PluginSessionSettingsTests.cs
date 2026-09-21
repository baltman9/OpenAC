using AcDream.Runtime.Plugins;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// The startup settings a client was given for each plugin: the one snapshot
/// both clients answer a plugin from, and the one reader that turns a file
/// naming them into it.
/// </summary>
public sealed class PluginSessionSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "acdream-plugin-settings",
        Guid.NewGuid().ToString("N"));

    public PluginSessionSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder that outlives the run is not a test failure.
        }
    }

    [Fact]
    public void AnUnconfiguredClientAnswersEveryPluginAnEmptyMap()
    {
        Assert.Empty(PluginSessionSettings.Empty.SessionSettingsFor("acdream.any"));
        Assert.Empty(PluginSessionSettings.Empty.PluginIds);
    }

    [Fact]
    public void EachPluginReadsItsOwnSettingsAndNobodyElses()
    {
        PluginSessionSettings settings = PluginSessionSettings.FromDeclared(
            new Dictionary<string, Dictionary<string, string>>
            {
                ["acdream.alpha"] = new() { ["startMacro"] = "true" },
                ["acdream.beta"] = new() { ["startMacro"] = "false" },
            });

        Assert.Equal("true", settings.SessionSettingsFor("acdream.alpha")["startMacro"]);
        Assert.Equal("false", settings.SessionSettingsFor("acdream.beta")["startMacro"]);
        Assert.Empty(settings.SessionSettingsFor("acdream.gamma"));
    }

    [Fact]
    public void EditingTheDeclaredMapAfterwardsChangesNothingAPluginReads()
    {
        var perPlugin = new Dictionary<string, string> { ["startMacro"] = "true" };
        var declared = new Dictionary<string, Dictionary<string, string>>
        {
            ["acdream.alpha"] = perPlugin,
        };

        PluginSessionSettings settings =
            PluginSessionSettings.FromDeclared(declared);
        perPlugin["startMacro"] = "false";
        declared["acdream.injected"] = new() { ["startMacro"] = "true" };

        Assert.Equal("true", settings.SessionSettingsFor("acdream.alpha")["startMacro"]);
        Assert.Empty(settings.SessionSettingsFor("acdream.injected"));
    }

    [Fact]
    public void APluginWithNoSettingsBehindItIsRefusedByName()
    {
        var declared = new Dictionary<string, Dictionary<string, string>>
        {
            ["acdream.alpha"] = null!,
        };

        Assert.Equal(
            "pluginSettings['acdream.alpha'] cannot be null.",
            PluginSessionSettings.DescribeFault(declared));
        PluginSessionSettingsException error =
            Assert.Throws<PluginSessionSettingsException>(
                () => PluginSessionSettings.FromDeclared(declared));
        Assert.Equal(
            "pluginSettings['acdream.alpha'] cannot be null.",
            error.Message);
    }

    [Fact]
    public void ASettingWithNoValueIsRefusedByName()
    {
        var declared = new Dictionary<string, Dictionary<string, string>>
        {
            ["acdream.alpha"] = new() { ["startMacro"] = null! },
        };

        Assert.Equal(
            "pluginSettings['acdream.alpha']['startMacro'] cannot be null.",
            PluginSessionSettings.DescribeFault(declared));
    }

    [Fact]
    public void AFileHoldingTheMapIsReadPluginByPlugin()
    {
        string path = Write(
            """
            {
              "acdream.alpha": { "startMacro": "true", "profile": "tank" },
              "acdream.beta": { "startMacro": "false" }
            }
            """);

        PluginSessionSettings settings = PluginSessionSettings.ReadFile(path);

        Assert.Equal("true", settings.SessionSettingsFor("acdream.alpha")["startMacro"]);
        Assert.Equal("tank", settings.SessionSettingsFor("acdream.alpha")["profile"]);
        Assert.Equal("false", settings.SessionSettingsFor("acdream.beta")["startMacro"]);
        Assert.Empty(settings.SessionSettingsFor("acdream.gamma"));
    }

    [Fact]
    public void AnEmptyMapIsAFileThatConfiguresNobody()
    {
        PluginSessionSettings settings =
            PluginSessionSettings.ReadFile(Write("{}"));

        Assert.Empty(settings.PluginIds);
        Assert.Empty(settings.SessionSettingsFor("acdream.alpha"));
    }

    /// <summary>
    /// The whole point of the option: a file the player named and the client
    /// could not read is a startup fault, not a run with no settings.
    /// </summary>
    [Fact]
    public void AMissingFileIsAFaultAndNotAnEmptyMap()
    {
        string path = Path.Combine(_directory, "absent.json");

        PluginSessionSettingsException error =
            Assert.Throws<PluginSessionSettingsException>(
                () => PluginSessionSettings.ReadFile(path));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotJsonIsAFaultNamingTheFile()
    {
        string path = Write("this is not json");

        PluginSessionSettingsException error =
            Assert.Throws<PluginSessionSettingsException>(
                () => PluginSessionSettings.ReadFile(path));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileOfTheWrongShapeIsAFaultNamingTheFile()
    {
        string path = Write("""{ "acdream.alpha": "startMacro" }""");

        PluginSessionSettingsException error =
            Assert.Throws<PluginSessionSettingsException>(
                () => PluginSessionSettings.ReadFile(path));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileHoldingNullIsAFaultNamingTheShapeItNeeds()
    {
        string path = Write("null");

        PluginSessionSettingsException error =
            Assert.Throws<PluginSessionSettingsException>(
                () => PluginSessionSettings.ReadFile(path));

        Assert.Contains(
            "object mapping plugin ids",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A hole in the file reads the same as a hole in a session file, so a
    /// player who moves a map from one client to the other is told the same
    /// thing about it.
    /// </summary>
    [Fact]
    public void AHoleInTheFileIsReportedInTheSameWordsAsAHoleInAMap()
    {
        string path = Write("""{ "acdream.alpha": null }""");

        PluginSessionSettingsException error =
            Assert.Throws<PluginSessionSettingsException>(
                () => PluginSessionSettings.ReadFile(path));

        Assert.EndsWith(
            "pluginSettings['acdream.alpha'] cannot be null.",
            error.Message,
            StringComparison.Ordinal);
    }

    private string Write(string json)
    {
        string path = Path.Combine(_directory, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
