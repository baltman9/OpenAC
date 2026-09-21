using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcDream.App.Configuration;
using AcDream.Headless.Configuration;
using LauncherConfig = AcDream.Launcher.Core.Launching;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The launcher writes the session document both clients read. Whatever it can
/// put in the document therefore decides what a launched session can be told,
/// and three fields both readers accept -- the words a session wants to be
/// found by, the startup settings its plugins get and the character options it
/// should arrive with -- could not be written at all. A launcher-started
/// session was quietly a smaller thing than a hand-written one.
///
/// The writer can carry all three now. These tests write a document holding
/// them and read it back with each client's own reader, so a field that
/// survives the writer but not a reader (or comes back different on one of
/// them) fails here rather than at someone's login.
/// </summary>
public sealed class LauncherSessionConfigRoundTripTests
{
    [Fact]
    public void BothClientsReadBackEveryFieldTheWriterCanNowEmit()
    {
        string path = WriteComposedDocument();

        (_, AcDream.App.Configuration.SessionDescriptor windowed) =
            SessionConfigurationLoader.Load(path);
        HeadlessSessionDescriptor windowless = Assert.Single(
            HeadlessConfigurationLoader.Load(path).Sessions)!;

        Assert.Equal(["shared-fixture-group", "second-word"], windowed.PluginTags);
        Assert.Equal(windowed.PluginTags, windowless.PluginTags);

        Assert.Equal("shared", windowed.PluginSettings!["ExamplePlugin"]["profile"]);
        Assert.Equal(
            windowed.PluginSettings["ExamplePlugin"],
            windowless.PluginSettings!["ExamplePlugin"]);

        Assert.True(windowed.CharacterOptions!["UseChargeAttack"]);
        Assert.Equal(windowed.CharacterOptions, windowless.CharacterOptions);
    }

    /// <summary>
    /// The writer spells the three fields the way both readers expect, and a
    /// reader that refuses unknown members would say so loudly -- but a
    /// misspelling that happened to be ignored would not, so the names are
    /// checked directly.
    /// </summary>
    [Fact]
    public void TheWriterSpellsTheThreeFieldsTheWayTheDocumentDoes()
    {
        JsonNode session = JsonNode
            .Parse(LauncherConfig.SessionConfigComposer.Serialize(ComposeDocument()))!
            ["sessions"]![0]!;

        Assert.NotNull(session["pluginTags"]);
        Assert.NotNull(session["pluginSettings"]);
        Assert.NotNull(session["characterOptions"]);
    }

    /// <summary>
    /// Nothing in the launcher fills the three in yet, so a document written
    /// for an ordinary launch is exactly what it was before: the fields are
    /// left out rather than written empty, which is what tells a client to
    /// fall back to its own startup options.
    /// </summary>
    [Fact]
    public void ADocumentThatDeclaresNoneOfThemLeavesThemOut()
    {
        JsonNode session = JsonNode
            .Parse(LauncherConfig.SessionConfigComposer.Serialize(new LauncherConfig.SessionConfigDocument
            {
                Sessions = [new LauncherConfig.SessionDescriptor
                {
                    Id = "plain",
                    Account = "account",
                }],
            }))!
            ["sessions"]![0]!;

        Assert.Null(session["pluginTags"]);
        Assert.Null(session["pluginSettings"]);
        Assert.Null(session["characterOptions"]);
    }

    private static LauncherConfig.SessionConfigDocument ComposeDocument()
    {
        JsonElement fixtureSession = JsonDocument
            .Parse(File.ReadAllText(SharedFixturePath()))
            .RootElement
            .GetProperty("sessions")[0];

        return new LauncherConfig.SessionConfigDocument
        {
            Process = new LauncherConfig.SessionProcessSettings
            {
                Content = new LauncherConfig.SessionContentDescriptor
                {
                    DatDirectory = "shared-fixture-dats",
                    PreparedAssetPath = "shared-fixture-dats/acdream.pak",
                },
            },
            Sessions =
            [
                new LauncherConfig.SessionDescriptor
                {
                    Id = "shared-fixture",
                    Endpoint = new LauncherConfig.SessionEndpointDescriptor
                    {
                        Host = "127.0.0.1",
                        Port = 9000,
                    },
                    Account = "sharedaccount",
                    Character = new LauncherConfig.SessionCharacterSelector
                    {
                        Name = "SharedToon",
                    },
                    Policy = new LauncherConfig.SessionPolicyDescriptor(),
                    StatusFile = "shared-fixture-status.jsonl",
                    PluginTags = Read<List<string>>(fixtureSession, "pluginTags"),
                    PluginSettings =
                        Read<Dictionary<string, Dictionary<string, string>>>(
                            fixtureSession,
                            "pluginSettings"),
                    CharacterOptions = Read<Dictionary<string, bool>>(
                        fixtureSession,
                        "characterOptions"),
                },
            ],
        };
    }

    private static string WriteComposedDocument()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"acdream-launcher-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, LauncherConfig.SessionConfigComposer.Serialize(ComposeDocument()));
        return path;
    }

    private static T Read<T>(JsonElement session, string member) =>
        session.GetProperty(member).Deserialize<T>()
        ?? throw new InvalidOperationException(
            $"The shared fixture has no '{member}' to round-trip.");

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
