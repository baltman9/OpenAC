using System.Security.Cryptography;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Updates;

/// <summary>
/// A launcher from before the single install folder updates itself into this
/// version: its transaction lives in its own data folder, and this version
/// finishes it there without copying or moving anything, then starts on its
/// own, empty install folder.
/// </summary>
public sealed class EarlierLayoutSelfUpdateTests : IDisposable
{
    private static readonly string[] PublicArguments =
        ["--update-manifest-uri", "http://127.0.0.1:43119/manifest.json"];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "openac-earlier-update-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The earlier launcher kept its update folder and its lock directly in
    /// its data folder. Mutation: putting either under app/ as this version
    /// does, or reading the data folder from the new root for a default
    /// start, fails this.
    /// </summary>
    [Fact]
    public void TheEarlierStateIsWhereTheEarlierLauncherWroteIt()
    {
        var platform = new ProfileEnvironment(Path.Combine(_root, "profile"));
        string earlierData = LegacyApplicationLayout.Detect(platform).DataDirectory;
        if (OperatingSystem.IsWindows())
            Assert.Equal(Path.Combine(platform.Local, "acdream"), earlierData);
        using var http = new HttpClient();

        LauncherSelfUpdateManager earlier = LauncherSelfUpdateManager.ForEarlierLayout(earlierData, http);

        Assert.Equal(Path.Combine(earlierData, "launcher-update"), earlier.RootDirectory);
        Assert.Equal(Path.Combine(earlierData, "launcher-update", "pending.json"), earlier.PendingPlanPath);
        Assert.Equal(Path.Combine(earlierData, "app", ".update-session.lock"), earlier.Barrier.LockPath);
        Assert.Equal(
            [earlierData],
            EarlierLayoutSelfUpdate.DataDirectoriesFor(ApplicationPathSet.Resolve(platform: platform), platform));

        // A data folder named the way the earlier launcher also read comes
        // first; the per-user default stays a candidate, since a folder named
        // only in this version's terms (a root) meant nothing to it.
        string named = Path.Combine(_root, "named data");
        Assert.Equal(
            [named, earlierData],
            EarlierLayoutSelfUpdate.DataDirectoriesFor(
                new ApplicationPathSet(Path.Combine(_root, "c"), named, Path.Combine(_root, "k")),
                platform));
    }

    /// <summary>
    /// The confirmer (this version, installed by the earlier launcher's
    /// helper) confirms in the earlier folder, waits for the helper to let
    /// the lease go, removes the transaction's files the helper could not
    /// (its own running copy), and only then starts on the new install
    /// folder. Mutation: confirming against the new folder, or skipping the
    /// wait and clean-up, fails this.
    /// </summary>
    [Fact]
    public async Task TheConfirmerFinishesTheEarlierTransactionWhereItLives()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        UpdateSessionBarrier.ExclusiveLease helperLease = harness.Earlier.Barrier.AcquireExclusive();
        SelfUpdatePlan applied = await harness.Earlier.ApplyPendingAsync(harness.Target);
        Assert.Equal(SelfUpdatePlanState.AwaitingConfirmation, applied.State);
        IReadOnlyDictionary<string, string> before = harness.SnapshotEarlierFolders();
        string stagedLauncher = Path.Combine(
            harness.Earlier.GetPayloadDirectory(applied.TransactionId),
            harness.LauncherName);

        // Stands in for the helper: it completes the transaction once
        // confirmed, lets the lease go, and exits a moment later; until then
        // its own executable in the transaction folder cannot be deleted.
        Task helper = Task.Run(async () =>
        {
            FileStream running = File.Open(stagedLauncher, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (!harness.Earlier.IsConfirmed(applied.TransactionId)
                       && DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(20);
                await harness.Earlier.CompleteConfirmedAsync(applied.TransactionId, harness.Target);
                helperLease.Dispose();
                await Task.Delay(300);
            }
            finally
            {
                running.Dispose();
                helperLease.Dispose();
            }
        });

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            [LauncherSelfUpdateBootstrap.ConfirmArgument, applied.TransactionId, .. PublicArguments],
            harness.Paths,
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            harness.Platform,
            TimeSpan.FromSeconds(20));
        await helper.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(result.ShouldExit);
        Assert.Equal(PublicArguments, result.RemainingArguments);
        Assert.Equal("new-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        harness.AssertEarlierTransactionGone(applied.TransactionId);
        Assert.Equal(before, harness.SnapshotEarlierFolders());
        harness.AssertNewRootIsEmpty();
    }

    /// <summary>
    /// A normal start of this launcher finds its own earlier update still
    /// waiting for confirmation (the helper died first) and finishes it in
    /// the earlier folder, also when this start names its install folder in
    /// terms the earlier launcher never read. Mutation: skipping the earlier
    /// folder on a normal start, or not looking in the per-user default when
    /// a root is named, fails this.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ANormalStartFinishesAnInterruptedEarlierUpdateWhereItLives(bool namedRoot)
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        SelfUpdatePlan applied;
        using (harness.Earlier.Barrier.AcquireExclusive())
        {
            applied = await harness.Earlier.ApplyPendingAsync(harness.Target);
        }

        IReadOnlyDictionary<string, string> before = harness.SnapshotEarlierFolders();
        ApplicationPathSet paths = namedRoot
            ? ApplicationPathSet.ForRoot(Path.Combine(_root, "named root"))
            : harness.Paths;

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            paths,
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            harness.Platform,
            TimeSpan.FromSeconds(5));

        Assert.False(result.ShouldExit);
        Assert.Equal(PublicArguments, result.RemainingArguments);
        Assert.Equal("new-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        harness.AssertEarlierTransactionGone(applied.TransactionId);
        Assert.Equal(before, harness.SnapshotEarlierFolders());
        EarlierHarness.AssertRootIsEmpty(paths);
    }

    /// <summary>
    /// An earlier update staged for another copy of the launcher belongs to
    /// that copy: this one starts normally and leaves it as it is. Mutation:
    /// recovering it regardless of its target fails this.
    /// </summary>
    [Fact]
    public async Task AnEarlierUpdateOfAnotherLauncherCopyIsLeftAlone()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        string pending = await File.ReadAllTextAsync(harness.Earlier.PendingPlanPath);
        string otherCopy = Path.Combine(_root, "another launcher");
        Directory.CreateDirectory(otherCopy);

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            harness.Paths,
            harness.Http,
            otherCopy,
            Path.Combine(otherCopy, harness.LauncherName),
            harness.Platform,
            TimeSpan.FromSeconds(5));

        Assert.False(result.ShouldExit);
        Assert.Equal(pending, await File.ReadAllTextAsync(harness.Earlier.PendingPlanPath));
        Assert.Equal("old-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
    }

    /// <summary>
    /// Without an earlier update nothing is created in the earlier folders.
    /// Mutation: taking the earlier lock on every start fails this.
    /// </summary>
    [Fact]
    public async Task WithoutAnEarlierUpdateTheEarlierFoldersAreNotTouched()
    {
        var platform = new ProfileEnvironment(Path.Combine(_root, "profile"));
        ApplicationPathSet paths = ApplicationPathSet.Resolve(platform: platform);
        string target = Path.Combine(_root, "launcher");
        Directory.CreateDirectory(target);
        using var http = new HttpClient();

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            paths,
            http,
            target,
            Path.Combine(target, "acdream-launcher.exe"),
            platform,
            TimeSpan.FromSeconds(5));

        Assert.False(result.ShouldExit);
        Assert.False(Directory.Exists(LegacyApplicationLayout.Detect(platform).DataDirectory));
        Assert.True(File.Exists(Path.Combine(paths.AppDirectory, ".update-session.lock")));
    }

    private sealed class EarlierHarness : IDisposable
    {
        private readonly LocalHttpFixture _server = new();

        private EarlierHarness(string root)
        {
            Platform = new ProfileEnvironment(Path.Combine(root, "profile"));
            Paths = ApplicationPathSet.Resolve(platform: Platform);
            LegacyApplicationLayout earlier = LegacyApplicationLayout.Detect(Platform);
            EarlierData = earlier.DataDirectory;
            EarlierConfig = earlier.ConfigDirectory;
            Target = Path.Combine(root, "launcher");
            Rid = LauncherRuntimeIdentity.DetectRid();
            LauncherName = "acdream-launcher"
                + (Rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty);
            LauncherPath = Path.Combine(Target, LauncherName);
            Earlier = LauncherSelfUpdateManager.ForEarlierLayout(EarlierData, Http);
        }

        public ProfileEnvironment Platform { get; }

        public ApplicationPathSet Paths { get; }

        public string EarlierData { get; }

        public string EarlierConfig { get; }

        public string Target { get; }

        public string Rid { get; }

        public string LauncherName { get; }

        public string LauncherPath { get; }

        public HttpClient Http { get; } = new();

        public LauncherSelfUpdateManager Earlier { get; }

        /// <summary>An earlier install with a player's files, and its launcher's staged update.</summary>
        public static async Task<EarlierHarness> StageAsync(string root)
        {
            var harness = new EarlierHarness(root);
            Directory.CreateDirectory(harness.Target);
            await File.WriteAllTextAsync(harness.LauncherPath, "old-launcher");
            await File.WriteAllTextAsync(Path.Combine(harness.Target, "support.dat"), "old-support");
            Write(harness.EarlierConfig, "launcher-profiles.json", "{ \"accounts\": 1 }");
            Write(harness.EarlierConfig, "settings.json", "{}");
            Write(harness.EarlierData, "app/current.json", "{ \"currentVersion\": \"0.1.16\" }");
            Write(harness.EarlierData, "app/0.1.16/AcDream.App.exe", "client");
            Write(harness.EarlierData, "install.json", "{}");
            Write(harness.EarlierData, "pak/acdream.pak", "pak");
            Write(harness.EarlierData, "plugins/acdream.mosstank/plugin.json", "{}");
            Write(harness.EarlierData, "cache/plugins.json", "{}");

            byte[] archive = UpdateTestData.LauncherZip(harness.Rid, "new-launcher");
            harness._server.Add("launcher.zip", archive);
            _ = await harness.Earlier.StageAsync(
                LauncherVersion.Parse("2.0.0"),
                harness.Rid,
                new ReleaseArtifact(
                    harness._server.UriFor("launcher.zip"),
                    UpdateTestData.Sha256(archive),
                    archive.LongLength),
                harness.Target,
                progress: null,
                CancellationToken.None);
            return harness;
        }

        /// <summary>
        /// Every file of the earlier folders with its hash, except the
        /// update's own folder and lock, which the update protocol owns.
        /// </summary>
        public IReadOnlyDictionary<string, string> SnapshotEarlierFolders()
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string folder in new[] { EarlierConfig, EarlierData })
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(Path.GetDirectoryName(folder)!, file);
                    if (file.StartsWith(Earlier.RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(file, Earlier.Barrier.LockPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    files[relative] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
                }
            }

            return files;
        }

        public void AssertEarlierTransactionGone(string transactionId)
        {
            Assert.False(File.Exists(Earlier.PendingPlanPath));
            Assert.False(Directory.Exists(Earlier.GetTransactionDirectory(transactionId)));
            Assert.Empty(Directory.EnumerateDirectories(Target, ".acdream-self-update-*"));
        }

        /// <summary>The profile's default install folder holds nothing but the start's lock.</summary>
        public void AssertNewRootIsEmpty() => AssertRootIsEmpty(Paths);

        /// <summary>The install folder holds nothing but the lock the start took.</summary>
        public static void AssertRootIsEmpty(ApplicationPathSet paths)
        {
            Assert.Equal(
                [Path.Combine("app", ".update-session.lock")],
                Directory.EnumerateFiles(paths.RootDirectory, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(paths.RootDirectory, file))
                    .ToArray());
        }

        public void Dispose()
        {
            Http.Dispose();
            _server.Dispose();
        }

        private static void Write(string folder, string relative, string content)
        {
            string path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}

/// <summary>
/// A per-user profile in a scratch folder, with no path variables set: the
/// earlier layout's folders and the default install folder both land in it.
/// The launcher fixture builds the same one from the same folder.
/// </summary>
internal sealed class ProfileEnvironment(string home) : IApplicationPathEnvironment
{
    public string Local => Path.Combine(home, "AppData", "Local");

    public string Roaming => Path.Combine(home, "AppData", "Roaming");

    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsMacOS => OperatingSystem.IsMacOS();

    public string CurrentDirectory => home;

    public string? GetEnvironmentVariable(string name) => name switch
    {
        "XDG_DATA_HOME" => Local,
        "XDG_CONFIG_HOME" => Roaming,
        "XDG_CACHE_HOME" => Path.Combine(home, ".cache"),
        _ => null,
    };

    public string GetFolderPath(Environment.SpecialFolder folder) => folder switch
    {
        Environment.SpecialFolder.LocalApplicationData => Local,
        Environment.SpecialFolder.ApplicationData => Roaming,
        Environment.SpecialFolder.UserProfile => home,
        _ => string.Empty,
    };
}
