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
