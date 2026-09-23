using AcDream.Platform;

namespace AcDream.Platform.Tests;

public sealed class ApplicationPathSetTests
{
    /// <summary>Mutation: renaming the Windows leaf from OpenAC fails this.</summary>
    [Fact]
    public void WindowsDefaultRootIsOpenAcUnderLocalApplicationData()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-runtime-windows"));
        var platform = new FixtureEnvironment(isWindows: true)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            ApplicationData = Path.Combine(root, "AppData", "Roaming"),
            LocalApplicationData = Path.Combine(root, "AppData", "Local"),
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        string expected = Path.Combine(platform.LocalApplicationData, "OpenAC");
        Assert.Equal(expected, paths.RootDirectory);
        Assert.Equal(expected, paths.DataDirectory);
        Assert.Equal(Path.Combine(expected, "settings"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(expected, "cache"), paths.CacheDirectory);
        Assert.Equal(ApplicationRootSource.Default, paths.RootSource);
    }

    /// <summary>Mutation: dropping "Application Support" from the macOS default fails this.</summary>
    [Fact]
    public void MacDefaultRootIsOpenAcUnderApplicationSupport()
    {
        string home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mac-home"));
        var platform = new FixtureEnvironment(isWindows: false)
        {
            IsMacOS = true,
            UserProfile = home,
            Variables = { ["XDG_DATA_HOME"] = Path.Combine(home, "linux-data") },
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        Assert.Equal(
            Path.Combine(home, "Library", "Application Support", "OpenAC"),
            paths.RootDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "cache"), paths.CacheDirectory);
    }

    /// <summary>Mutation: ignoring XDG_DATA_HOME, or using "OpenAC" on Linux, fails this.</summary>
    [Fact]
    public void LinuxDefaultRootFollowsXdgDataHome()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-runtime-xdg"));
        var platform = new FixtureEnvironment(isWindows: false)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            UserProfile = Path.Combine(root, "home"),
            Variables =
            {
                ["XDG_CONFIG_HOME"] = Path.Combine(root, "cfg"),
                ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
                ["XDG_CACHE_HOME"] = Path.Combine(root, "cache"),
            },
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        Assert.Equal(Path.Combine(root, "data", "openac"), paths.RootDirectory);
        Assert.Equal(Path.Combine(root, "data", "openac", "settings"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(root, "data", "openac", "cache"), paths.CacheDirectory);
    }

    /// <summary>Mutation: a missing ".local/share" fallback fails this.</summary>
    [Fact]
    public void LinuxDefaultRootFallsBackToLocalShare()
    {
        string home = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "linux-home"));
        var platform = new FixtureEnvironment(isWindows: false) { UserProfile = home };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        Assert.Equal(Path.Combine(home, ".local", "share", "openac"), paths.RootDirectory);
    }

    /// <summary>Mutation: moving any one member off its folder under the root fails this.</summary>
    [Fact]
    public void EveryMemberLivesInItsFolderUnderTheRoot()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "openac-members"));

        ApplicationPathSet paths = ApplicationPathSet.ForRoot(root);

        Assert.Equal(root, paths.RootDirectory);
        Assert.Equal(Path.Combine(root, "app"), paths.AppDirectory);
        Assert.Equal(Path.Combine(root, "data"), paths.GameDataDirectory);
        Assert.Equal(Path.Combine(root, "settings"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(root, "settings", "settings.json"), paths.SettingsFile);
        Assert.Equal(Path.Combine(root, "settings", "keybinds.json"), paths.KeyBindingsFile);
        Assert.Equal(Path.Combine(root, "plugins"), paths.PluginsDirectory);
        Assert.Equal(Path.Combine(root, "plugins"), paths.PluginStorageDirectory);
        Assert.Equal(
            Path.Combine(root, "plugins", "acdream.mosstank", "files"),
            paths.PluginFilesDirectory("acdream.mosstank"));
        Assert.Equal(Path.Combine(root, "vtank"), paths.VtankProfilesDirectory);
        Assert.Equal(Path.Combine(root, "logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(root, "logs", "crash"), paths.CrashReportsDirectory);
        Assert.Equal(Path.Combine(root, "logs", "launcher"), paths.LauncherSessionsDirectory);
        Assert.Equal(Path.Combine(root, "cache"), paths.CacheDirectory);
        Assert.Equal(Path.Combine(root, "cache", "diagnostics"), paths.DiagnosticsDirectory);
        Assert.Equal(Path.Combine(root, "cache", "plugin-peers"), paths.PluginPeersDirectory);
        Assert.Equal(Path.Combine(root, "layout.json"), paths.LayoutMarkerFile);
    }

    /// <summary>Mutation: reading the pointer before ACDREAM_ROOT_DIR fails this.</summary>
    [Fact]
    public void RootEnvironmentVariableWinsOverThePointer()
    {
        FixtureEnvironment platform = WindowsFixture("root-env");
        platform.Files[PointerPath(platform)] = PointerText(Path.Combine(platform.Base, "pointed"));
        platform.Variables["ACDREAM_ROOT_DIR"] = Path.Combine(platform.Base, "from-env");

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        Assert.Equal(Path.Combine(platform.Base, "from-env"), paths.RootDirectory);
        Assert.Equal(ApplicationRootSource.Environment, paths.RootSource);
    }

    /// <summary>Mutation: skipping the pointer read fails this.</summary>
    [Fact]
    public void PointerInTheDefaultRootNamesTheRoot()
    {
        FixtureEnvironment platform = WindowsFixture("pointer");
        string pointed = Path.Combine(platform.Base, "Games", "OpenAC");
        platform.Files[PointerPath(platform)] = PointerText(pointed);

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        Assert.Equal(pointed, paths.RootDirectory);
        Assert.Equal(Path.Combine(pointed, "settings"), paths.ConfigDirectory);
        Assert.Equal(ApplicationRootSource.Pointer, paths.RootSource);
    }

    /// <summary>Mutation: silently ignoring a broken pointer fails this.</summary>
    [Fact]
    public void BrokenPointerIsReportedRatherThanIgnored()
    {
        FixtureEnvironment platform = WindowsFixture("broken-pointer");
        platform.Files[PointerPath(platform)] = "{ \"version\": 1, \"root\": \"relative\" }";

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => ApplicationPathSet.Resolve(platform: platform));
        Assert.Contains("root.json", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Mutation: letting ACDREAM_ROOT_DIR beat ACDREAM_CONFIG_DIR, or dropping
    /// the member variables, fails this.
    /// </summary>
    [Fact]
    public void MemberVariablesStillOverrideTheirOwnMemberOverTheRootVariable()
    {
        FixtureEnvironment platform = WindowsFixture("member-env");
        platform.Variables["ACDREAM_ROOT_DIR"] = "root";
        platform.Variables["ACDREAM_CONFIG_DIR"] = "capture-config";
        platform.Variables["ACDREAM_CACHE_DIR"] = "capture-cache";

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        Assert.Equal(Path.Combine(platform.Base, "root"), paths.RootDirectory);
        Assert.Equal(Path.Combine(platform.Base, "capture-config"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(platform.Base, "capture-cache"), paths.CacheDirectory);
    }

    /// <summary>Mutation: keeping RootSource at Default when a member variable is set fails this.</summary>
    [Fact]
    public void MemberVariablesIsolateAllMutableStateAndMarkTheSetAsNotDefault()
    {
        FixtureEnvironment platform = WindowsFixture("isolated");
        platform.Variables["ACDREAM_CONFIG_DIR"] = "capture-config";
        platform.Variables["ACDREAM_DATA_DIR"] = "capture-data";
        platform.Variables["ACDREAM_CACHE_DIR"] = "capture-cache";

        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);

        Assert.Equal(Path.Combine(platform.Base, "capture-config"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(platform.Base, "capture-data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(platform.Base, "capture-cache"), paths.CacheDirectory);
        Assert.Equal(ApplicationRootSource.Environment, paths.RootSource);
    }

    /// <summary>
    /// Mutation: letting environment member variables leak under an explicit
    /// root argument fails this.
    /// </summary>
    [Fact]
    public void ExplicitRootIgnoresTheEnvironment()
    {
        FixtureEnvironment platform = WindowsFixture("explicit-root");
        platform.Variables["ACDREAM_ROOT_DIR"] = "environment-root";
        platform.Variables["ACDREAM_CONFIG_DIR"] = "environment-config";

        ApplicationPathSet paths = ApplicationPathSet.Resolve(
            platform: platform,
            rootDirectory: "explicit-root");

        string root = Path.Combine(platform.Base, "explicit-root");
        Assert.Equal(root, paths.RootDirectory);
        Assert.Equal(Path.Combine(root, "settings"), paths.ConfigDirectory);
        Assert.Equal(ApplicationRootSource.Explicit, paths.RootSource);
    }

    /// <summary>Mutation: letting the explicit root beat an explicit member fails this.</summary>
    [Fact]
    public void ExplicitMemberWinsOverExplicitRoot()
    {
        FixtureEnvironment platform = WindowsFixture("explicit-member");

        ApplicationPathSet paths = ApplicationPathSet.Resolve(
            configDirectory: "explicit-config",
            platform: platform,
            rootDirectory: "explicit-root");

        Assert.Equal(Path.Combine(platform.Base, "explicit-config"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(platform.Base, "explicit-root"), paths.DataDirectory);
    }

    /// <summary>Mutation: letting environment members beat explicit members fails this.</summary>
    [Fact]
    public void ExplicitArgumentsOverrideAutomationEnvironmentRoots()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-explicit-paths"));
        var platform = new FixtureEnvironment(isWindows: false)
        {
            CurrentDirectoryValue = root,
            UserProfile = Path.Combine(root, "home"),
            Variables =
            {
                ["ACDREAM_CONFIG_DIR"] = "environment-config",
                ["ACDREAM_DATA_DIR"] = "environment-data",
                ["ACDREAM_CACHE_DIR"] = "environment-cache",
            },
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(
            "explicit-config",
            "explicit-data",
            "explicit-cache",
            platform);

        Assert.Equal(Path.Combine(root, "explicit-config"), paths.ConfigDirectory);
        Assert.Equal(Path.Combine(root, "explicit-data"), paths.DataDirectory);
        Assert.Equal(Path.Combine(root, "explicit-cache"), paths.CacheDirectory);
        Assert.Equal(ApplicationRootSource.Explicit, paths.RootSource);
    }

    /// <summary>Mutation: skipping normalization against the current directory fails this.</summary>
    [Fact]
    public void RelativeOverridesAreNormalizedAgainstTheCurrentDirectory()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream runtime paths"));
        var platform = new FixtureEnvironment(isWindows: false)
        {
            CurrentDirectoryValue = Path.Combine(root, "work"),
            UserProfile = Path.Combine(root, "home"),
        };

        ApplicationPathSet paths = ApplicationPathSet.Resolve(
            "relative config",
            "dåta",
            "cache",
            platform);

        Assert.Equal(
            Path.GetFullPath("relative config", platform.CurrentDirectoryValue),
            paths.ConfigDirectory);
        Assert.Equal(
            Path.GetFullPath("dåta", platform.CurrentDirectoryValue),
            paths.DataDirectory);
        Assert.Equal(
            Path.GetFullPath("cache", platform.CurrentDirectoryValue),
            paths.CacheDirectory);
    }

    /// <summary>Mutation: writing a pointer that names the default itself fails this.</summary>
    [Fact]
    public void PointerWriteRoundTripsAndPointingAtTheDefaultRemovesIt()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "openac-pointer-" + Guid.NewGuid().ToString("N"));
        try
        {
            string defaultRoot = Path.Combine(scratch, "default");
            string moved = Path.Combine(scratch, "moved");

            ApplicationRootPointer.Write(defaultRoot, moved);
            string written = File.ReadAllText(ApplicationRootPointer.PathFor(defaultRoot));
            Assert.Equal(moved, ApplicationRootPointer.Parse(written));

            ApplicationRootPointer.Write(defaultRoot, defaultRoot);
            Assert.False(File.Exists(ApplicationRootPointer.PathFor(defaultRoot)));
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>Mutation: accepting another version or a relative root fails this.</summary>
    [Theory]
    [InlineData("{ \"version\": 2, \"root\": \"C:\\\\x\" }")]
    [InlineData("{ \"version\": 1, \"root\": \"x\" }")]
    [InlineData("{ \"version\": 1 }")]
    [InlineData("not json")]
    public void PointerParseRejectsAnythingButVersionOneWithAnAbsoluteRoot(string text)
    {
        Assert.Null(ApplicationRootPointer.Parse(text));
    }

    private static FixtureEnvironment WindowsFixture(string name)
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "openac-resolve-" + name));
        return new FixtureEnvironment(isWindows: true)
        {
            Base = root,
            CurrentDirectoryValue = root,
            ApplicationData = Path.Combine(root, "Roaming"),
            LocalApplicationData = Path.Combine(root, "Local"),
        };
    }

    private static string PointerPath(FixtureEnvironment platform) =>
        Path.Combine(platform.LocalApplicationData, "OpenAC", "root.json");

    private static string PointerText(string root) =>
        "{ \"version\": 1, \"root\": " + System.Text.Json.JsonSerializer.Serialize(root) + " }";

    private sealed class FixtureEnvironment(bool isWindows)
        : IApplicationPathEnvironment
    {
        public string Base { get; init; } = Path.GetTempPath();

        public bool IsWindows { get; } = isWindows;

        public bool IsMacOS { get; init; }

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

        public Dictionary<string, string> Files { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public string? GetEnvironmentVariable(string name) =>
            Variables.TryGetValue(name, out string? value) ? value : null;

        public string GetFolderPath(Environment.SpecialFolder folder) =>
            folder switch
            {
                Environment.SpecialFolder.UserProfile => UserProfile,
                Environment.SpecialFolder.ApplicationData => ApplicationData,
                Environment.SpecialFolder.LocalApplicationData => LocalApplicationData,
                _ => string.Empty,
            };

        public string? ReadTextFile(string path) =>
            Files.TryGetValue(path, out string? text) ? text : null;
    }
}
