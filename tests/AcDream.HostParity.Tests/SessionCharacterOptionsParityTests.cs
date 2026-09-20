using System.Runtime.CompilerServices;
using AcDream.App.Configuration;
using AcDream.Core.Net.Messages;
using AcDream.Headless.Configuration;
using AcDream.Runtime.Gameplay;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The character options a session document declares belong to the character
/// on the server, so a document that names them is asking for the character
/// itself to arrive a certain way. That has to mean the same thing whichever
/// client reads the document.
///
/// It did not. The windowless client checked the names, parsed them and seeded
/// them on login; the windowed client accepted the field and dropped it. The
/// same file therefore produced two different characters, and a plugin reading
/// the options saw different answers depending on which client it was loaded
/// into. Both clients now check the same list, read the map with the same
/// parser and hand it to the same runtime seeder.
/// </summary>
public sealed class SessionCharacterOptionsParityTests
{
    [Fact]
    public void BothClientsReadTheSameDeclaredOptionsOutOfTheSameDocument()
    {
        string path = SharedFixturePath();

        (_, SessionDescriptor windowed) = SessionConfigurationLoader.Load(path);
        HeadlessSessionDescriptor windowless = Assert.Single(
            HeadlessConfigurationLoader.Load(path).Sessions)!;

        Dictionary<CharacterOptionId, bool> windowedOptions =
            RuntimeDeclaredCharacterOptions.Parse(
                windowed.Id,
                windowed.CharacterOptions);
        Dictionary<CharacterOptionId, bool> windowlessOptions =
            RuntimeDeclaredCharacterOptions.Parse(
                windowless.Id,
                windowless.CharacterOptions);

        Assert.NotEmpty(windowedOptions);
        Assert.Equal(windowlessOptions, windowedOptions);
    }

    /// <summary>
    /// The windowed client used to hold nothing here: the field was read off
    /// the document and thrown away. This is what makes the seeding reachable
    /// on that client at all.
    /// </summary>
    [Fact]
    public void TheWindowedClientCarriesTheDeclaredOptionsIntoItsStartupOptions()
    {
        (SessionConfiguration configuration, SessionDescriptor session) =
            SessionConfigurationLoader.Load(SharedFixturePath());

        AcDream.App.RuntimeOptions options =
            AcDream.App.RuntimeOptions.FromSessionConfig(
                "dats",
                _ => null,
                SharedFixturePath(),
                configuration,
                session,
                resolvedPassword: "password");

        Assert.True(
            options.DeclaredCharacterOptions[CharacterOptionId.UseChargeAttack]);
    }

    /// <summary>
    /// An ordinary launch has no session document, and a player's own saved
    /// options are never overruled by one.
    /// </summary>
    [Fact]
    public void AClientStartedWithoutADocumentDeclaresNoCharacterOptions()
    {
        AcDream.App.RuntimeOptions options =
            AcDream.App.RuntimeOptions.FromEnvironment("dats");

        Assert.Empty(options.DeclaredCharacterOptions);
    }

    [Theory]
    [InlineData("\"NotAnOption\": true")]
    [InlineData("\"ShowTooltips\": true")]
    public void BothClientsRefuseAnOptionThatIsNotDeclarable(string declaration)
    {
        string path = WriteDocument(declaration);

        SessionConfigurationException windowed =
            Assert.Throws<SessionConfigurationException>(
                () => SessionConfigurationLoader.Load(path));
        HeadlessConfigurationException windowless =
            Assert.Throws<HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(path));

        Assert.Equal(windowless.Message, windowed.Message);
        Assert.Contains("not a declarable character option", windowed.Message);
    }

    [Fact]
    public void BothClientsRefuseAPairThatCannotHoldAtOnce()
    {
        string path = WriteDocument(
            "\"IgnoreFellowshipRequests\": true, "
            + "\"FellowshipAutoAcceptRequests\": true");

        SessionConfigurationException windowed =
            Assert.Throws<SessionConfigurationException>(
                () => SessionConfigurationLoader.Load(path));
        HeadlessConfigurationException windowless =
            Assert.Throws<HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(path));

        Assert.Equal(windowless.Message, windowed.Message);
        Assert.Contains("exclude one another", windowed.Message);
    }

    private static string WriteDocument(string characterOptions)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"acdream-parity-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "version": 1,
              "sessions": [
                {
                  "id": "one",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "character": { "name": "SharedToon" },
                  "policy": { "id": "idle" },
                  "credential": { "provider": "standardInput", "reference": "session" },
                  "characterOptions": { {{characterOptions}} }
                }
              ]
            }
            """);
        return path;
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
