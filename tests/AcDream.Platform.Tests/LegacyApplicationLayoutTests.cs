using AcDream.Platform;

namespace AcDream.Platform.Tests;

public sealed class LegacyApplicationLayoutTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "openac-earlier-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
            Directory.Delete(_scratch, recursive: true);
    }

    /// <summary>
    /// The Windows folders an earlier version used: settings in the roaming
    /// profile, data (and the launcher's own update state) in the local one.
    /// Mutation: naming another folder fails this.
    /// </summary>
    [Fact]
    public void WindowsNamesTheRoamingAndLocalAcdreamFolders()
    {
        var platform = new WindowsEnvironment(_scratch);

        LegacyApplicationLayout old = LegacyApplicationLayout.Detect(platform);

        Assert.Equal(Path.Combine(_scratch, "Roaming", "acdream"), old.ConfigDirectory);
        Assert.Equal(Path.Combine(_scratch, "Local", "acdream"), old.DataDirectory);
        Assert.Equal(Path.Combine(_scratch, "Local", "acdream", "cache"), old.CacheDirectory);
        Assert.Equal(
            [Path.Combine(_scratch, "Roaming", "acdream"), Path.Combine(_scratch, "Local", "acdream")],
            old.TopLevelRoots());
    }

    /// <summary>
    /// Only folders that exist are named, only beside an install found the
    /// ordinary way; a root named on the command line or in the environment
    /// is isolated and gets none. Mutation: listing folders that are not
    /// there, or listing them for a named root, fails this.
    /// </summary>
    [Fact]
    public void ExistingFoldersAreNamedOnlyForAnOrdinaryRoot()
    {
        var platform = new WindowsEnvironment(_scratch);
        string local = Path.Combine(_scratch, "Local", "acdream");
        Directory.CreateDirectory(Path.Combine(local, "app"));
        string root = Path.Combine(_scratch, "Local", "OpenAC");

        Assert.Equal(
            [local],
            LegacyApplicationLayout.ExistingFolders(
                ApplicationPathSet.ForRoot(root, ApplicationRootSource.Default),
                platform));
        Assert.Equal(
            [local],
            LegacyApplicationLayout.ExistingFolders(
                ApplicationPathSet.ForRoot(root, ApplicationRootSource.Pointer),
                platform));
        Assert.Empty(LegacyApplicationLayout.ExistingFolders(
            ApplicationPathSet.ForRoot(root, ApplicationRootSource.Explicit),
            platform));
        Assert.Empty(LegacyApplicationLayout.ExistingFolders(
            ApplicationPathSet.ForRoot(root, ApplicationRootSource.Environment),
            platform));
    }

    /// <summary>
    /// Every entry only an earlier version's data folder has at its top makes
    /// a folder unusable as the install folder; a full install folder of this
    /// version has none of them. Mutation: dropping any entry from the
    /// signature, or refusing a folder that has none, fails this.
    /// </summary>
    [Theory]
    [InlineData("launcher-update", true)]
    [InlineData("pak", true)]
    [InlineData("install.json", false)]
    [InlineData("install.verification.json", false)]
    [InlineData("crash-reports", true)]
    public void AnEarlierDataFolderIsRefusedAsTheInstallFolder(string entry, bool folder)
    {
        string root = Path.Combine(_scratch, "named");
        ApplicationPathSet paths = ApplicationPathSet.ForRoot(root);
        foreach (string member in new[]
                 {
                     paths.AppDirectory, paths.GameDataDirectory, paths.ConfigDirectory,
                     paths.PluginsDirectory, paths.VtankProfilesDirectory, paths.LogsDirectory,
                     paths.CrashReportsDirectory, paths.CacheDirectory, paths.JournalDirectory,
                     paths.ScreenshotsDirectory, Path.Combine(paths.AppDirectory, "launcher-update"),
                     Path.Combine(paths.GameDataDirectory, "pak"),
                 })
        {
            Directory.CreateDirectory(member);
        }

        File.WriteAllText(Path.Combine(paths.GameDataDirectory, "install.json"), "{}");
        Assert.Null(LegacyApplicationLayout.RefusalToUseAsInstallFolder(paths));

        if (folder)
            Directory.CreateDirectory(Path.Combine(root, entry));
        else
            File.WriteAllText(Path.Combine(root, entry), "{}");

        string? refusal = LegacyApplicationLayout.RefusalToUseAsInstallFolder(paths);
        Assert.NotNull(refusal);
        Assert.Contains(root, refusal, StringComparison.Ordinal);
        Assert.Contains(entry, refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// An earlier folder the running launcher sits in is not offered for
    /// deletion. Mutation: ignoring the launcher's folder fails this.
    /// </summary>
    [Fact]
    public void AnEarlierFolderHoldingTheLauncherIsNotListed()
    {
        var platform = new WindowsEnvironment(_scratch);
        string roaming = Path.Combine(_scratch, "Roaming", "acdream");
        string local = Path.Combine(_scratch, "Local", "acdream");
        Directory.CreateDirectory(roaming);
        Directory.CreateDirectory(Path.Combine(local, "launcher"));
        ApplicationPathSet paths = ApplicationPathSet.ForRoot(
            Path.Combine(_scratch, "Local", "OpenAC"),
            ApplicationRootSource.Default);

        Assert.Equal(
            [roaming],
            LegacyApplicationLayout.ExistingFolders(paths, platform, Path.Combine(local, "launcher")));
        Assert.Equal(
            [roaming, local],
            LegacyApplicationLayout.ExistingFolders(paths, platform, Path.Combine(_scratch, "elsewhere")));
    }

    private sealed class WindowsEnvironment(string scratch) : IApplicationPathEnvironment
    {
        public bool IsWindows => true;

        public string CurrentDirectory => scratch;

        public string? GetEnvironmentVariable(string name) => null;

        public string GetFolderPath(Environment.SpecialFolder folder) => folder switch
        {
            Environment.SpecialFolder.ApplicationData => Path.Combine(scratch, "Roaming"),
            Environment.SpecialFolder.LocalApplicationData => Path.Combine(scratch, "Local"),
            Environment.SpecialFolder.UserProfile => scratch,
            _ => string.Empty,
        };
    }
}
