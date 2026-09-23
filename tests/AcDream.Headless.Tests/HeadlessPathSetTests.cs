using AcDream.Headless.Configuration;
using AcDream.Headless.Platform;

namespace AcDream.Headless.Tests;

public sealed class HeadlessPathSetTests
{
    /// <summary>
    /// A bot resolves the same install root the window and the launcher do.
    /// Mutation: resolving the bot's folders any other way than through the
    /// shared resolver (the old per-member XDG folders) fails this.
    /// </summary>
    [Fact]
    public void LinuxUsesTheOpenAcRootUnderXdgDataHome()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-xdg"));
        var platform = new FixturePlatform(isWindows: false)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            UserProfile = Path.Combine(root, "home", "bot"),
            Variables =
            {
                ["XDG_CONFIG_HOME"] = Path.Combine(root, "cfg"),
                ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
                ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            },
        };

        HeadlessPathSet paths = HeadlessPathSet.Resolve(
            new HeadlessPathOverrides(),
            platform);

        string openac = Path.Combine(root, "data", "openac");
        Assert.Equal(Path.Combine(openac, "settings"), paths.ConfigDirectory);
        Assert.Equal(openac, paths.DataDirectory);
        Assert.Equal(Path.Combine(openac, "cache"), paths.CacheDirectory);
    }

    [Fact]
    public void LinuxFallsBackToHomeAndNormalizesOverrides()
    {
        var platform = new FixturePlatform(isWindows: false)
        {
            CurrentDirectoryValue = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "acdream work")),
            UserProfile = Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), "bot home")),
        };

        HeadlessPathSet paths = HeadlessPathSet.Resolve(
            new HeadlessPathOverrides(
                ConfigDirectory: "relative cfg",
                DataDirectory: "dåta",
                CacheDirectory: "cache"),
            platform);

        Assert.Equal(
            Path.GetFullPath("relative cfg", platform.CurrentDirectoryValue),
            paths.ConfigDirectory);
        Assert.Equal(
            Path.GetFullPath("dåta", platform.CurrentDirectoryValue),
            paths.DataDirectory);
        Assert.Equal(
            Path.GetFullPath("cache", platform.CurrentDirectoryValue),
            paths.CacheDirectory);
    }

    /// <summary>Mutation: a Windows default other than LocalAppData\OpenAC fails this.</summary>
    [Fact]
    public void WindowsUsesTheOpenAcRootUnderLocalAppData()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-windows"));
        var platform = new FixturePlatform(isWindows: true)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            ApplicationData = Path.Combine(root, "AppData", "Roaming"),
            LocalApplicationData = Path.Combine(root, "AppData", "Local"),
            UserProfile = Path.Combine(root, "Users", "bot"),
        };

        HeadlessPathSet paths = HeadlessPathSet.Resolve(
            new HeadlessPathOverrides(),
            platform);

        Assert.EndsWith(
            Path.Combine("AppData", "Local", "OpenAC", "settings"),
            paths.ConfigDirectory);
        Assert.EndsWith(
            Path.Combine("AppData", "Local", "OpenAC"),
            paths.DataDirectory);
        Assert.EndsWith(
            Path.Combine("AppData", "Local", "OpenAC", "cache"),
            paths.CacheDirectory);
    }

    /// <summary>
    /// The per-plugin folders a bot process uses, spelled the same way the
    /// graphical host spells them: plugin code, each plugin's own files
    /// inside its folder, shared VTank profiles beside them, and the peer
    /// notes in the cache. Mutation: keeping plugin storage under settings,
    /// or peers under the root, fails this.
    /// </summary>
    [Fact]
    public void PluginFoldersLiveUnderTheRootAndPeersInTheCache()
    {
        var paths = new HeadlessPathSet(
            Path.Combine("root", "settings"),
            "root",
            Path.Combine("root", "cache"));

        Assert.Equal(Path.Combine("root", "plugins"), paths.PluginsDirectory);
        Assert.Equal(Path.Combine("root", "vtank"), paths.VtankProfilesDirectory);
        Assert.Equal(Path.Combine("root", "plugins"), paths.PluginStorageDirectory);
        Assert.Equal(Path.Combine("root", "cache", "plugin-peers"), paths.PluginPeersDirectory);
    }

    /// <summary>Mutation: a macOS default other than Application Support/OpenAC fails this.</summary>
    [Fact]
    public void MacOSUsesApplicationSupportLikeTheGraphicalClient()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-macos"));
        var platform = new FixturePlatform(isWindows: false, isMacOS: true)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            UserProfile = Path.Combine(root, "Users", "bot"),
        };

        HeadlessPathSet paths = HeadlessPathSet.Resolve(
            new HeadlessPathOverrides(),
            platform);

        Assert.Equal(
            Path.Combine(root, "Users", "bot", "Library", "Application Support", "OpenAC"),
            paths.DataDirectory);
    }

    /// <summary>
    /// A bot configuration naming a root uses exactly that folder, whatever
    /// the shell exported. Mutation: not passing the root through to the
    /// resolver fails this.
    /// </summary>
    [Fact]
    public void ConfiguredRootWinsOverTheEnvironment()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "acdream-bot-root"));
        var platform = new FixturePlatform(isWindows: true)
        {
            CurrentDirectoryValue = root,
            LocalApplicationData = Path.Combine(root, "Local"),
            Variables =
            {
                ["ACDREAM_ROOT_DIR"] = Path.Combine(root, "from-env"),
                ["ACDREAM_CONFIG_DIR"] = Path.Combine(root, "env-config"),
            },
        };

        HeadlessPathSet paths = HeadlessPathSet.Resolve(
            new HeadlessPathOverrides(RootDirectory: "bot-root"),
            platform);

        Assert.Equal(Path.Combine(root, "bot-root"), paths.DataDirectory);
        Assert.Equal(Path.Combine(root, "bot-root", "settings"), paths.ConfigDirectory);
    }

    /// <summary>Mutation: merging a command-line root member by member fails this.</summary>
    [Fact]
    public void CommandLineRootReplacesTheConfiguredFolders()
    {
        var configured = new HeadlessPathOverrides(
            ConfigDirectory: "configured-config",
            RootDirectory: "configured-root");

        HeadlessPathOverrides merged = configured.Merge(
            new HeadlessPathOverrides(RootDirectory: "cli-root"));

        Assert.Equal(new HeadlessPathOverrides(RootDirectory: "cli-root"), merged);
    }

    /// <summary>
    /// bot.json names the install root under process.paths. Mutation:
    /// removing RootDirectory from the overrides record makes the strict
    /// loader refuse the document, failing this.
    /// </summary>
    [Fact]
    public void BotConfigurationCanNameTheRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"acdream-root-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """{"version":1,"process":{"paths":{"rootDirectory":"D:/OpenAC"}},"sessions":[]}""");
        try
        {
            HeadlessConfiguration configuration = HeadlessConfigurationLoader.Load(path);

            Assert.Equal("D:/OpenAC", configuration.Process.Paths.RootDirectory);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Mutation: dropping the --root-dir case from the parser fails this.</summary>
    [Fact]
    public void CommandLineAcceptsRootDir()
    {
        HeadlessCommandLine command = HeadlessCommandLine.Parse(
            ["run", "--config", "bot.json", "--root-dir", "D:/OpenAC"]);

        Assert.Equal("D:/OpenAC", command.Paths.RootDirectory);
    }

    [Fact]
    public void HostEnvironmentReportsTheRunningOperatingSystem()
    {
        IHeadlessPlatformEnvironment platform = HeadlessPlatformEnvironment.Instance;

        Assert.Equal(OperatingSystem.IsWindows(), platform.IsWindows);
        Assert.Equal(OperatingSystem.IsMacOS(), platform.IsMacOS);
    }

    private sealed class FixturePlatform(bool isWindows, bool isMacOS = false)
        : IHeadlessPlatformEnvironment
    {
        public bool IsWindows { get; } = isWindows;
        public bool IsMacOS { get; } = isMacOS;
        public string CurrentDirectoryValue { get; init; } =
            Environment.CurrentDirectory;
        public string CurrentDirectory => CurrentDirectoryValue;
        public string UserProfile { get; init; } =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public string ApplicationData { get; init; } =
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public string LocalApplicationData { get; init; } =
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public Dictionary<string, string> Variables { get; } =
            new(StringComparer.Ordinal);

        public string? GetEnvironmentVariable(string name) =>
            Variables.TryGetValue(name, out string? value)
                ? value
                : null;

        public string GetFolderPath(Environment.SpecialFolder folder) =>
            folder switch
            {
                System.Environment.SpecialFolder.UserProfile => UserProfile,
                System.Environment.SpecialFolder.ApplicationData =>
                    ApplicationData,
                System.Environment.SpecialFolder.LocalApplicationData =>
                    LocalApplicationData,
                _ => string.Empty,
            };
    }
}
