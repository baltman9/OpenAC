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
            IDisposable running = RunningHelperCopy.Hold(stagedLauncher);
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
            Settings(harness.Platform, TimeSpan.FromSeconds(20)));
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
            Settings(harness.Platform, TimeSpan.FromSeconds(5)));

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
            Settings(harness.Platform, TimeSpan.FromSeconds(5)));

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
            Settings(platform, TimeSpan.FromSeconds(5)));

        Assert.False(result.ShouldExit);
        Assert.False(Directory.Exists(LegacyApplicationLayout.Detect(platform).DataDirectory));
        Assert.True(File.Exists(Path.Combine(paths.AppDirectory, ".update-session.lock")));
    }

    /// <summary>
    /// The data folder an earlier launcher ran with (<c>--data-dir</c>) named
    /// as this version's install folder is refused with the reason, after the
    /// earlier update in it is finished, and nothing in it changes. Mutation:
    /// dropping the refusal fails this.
    /// </summary>
    [Fact]
    public async Task AnEarlierDataFolderNamedAsTheInstallFolderIsRefusedAndLeftAsItIs()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        SelfUpdatePlan applied;
        using (harness.Earlier.Barrier.AcquireExclusive())
        {
            applied = await harness.Earlier.ApplyPendingAsync(harness.Target);
        }

        var named = new ApplicationPathSet(
            harness.EarlierConfig,
            harness.EarlierData,
            Path.Combine(harness.EarlierData, "cache"));
        IReadOnlyDictionary<string, string> before = harness.SnapshotEarlierFolders();

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            named,
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            Settings(harness.Platform, TimeSpan.FromSeconds(5)));

        Assert.True(result.ShouldExit);
        Assert.Equal(LauncherSelfUpdateBootstrap.RefusedExitCode, result.ExitCode);
        Assert.Contains(harness.EarlierData, result.Refusal, StringComparison.Ordinal);
        Assert.Contains("new, empty folder", result.Refusal, StringComparison.Ordinal);
        Assert.Equal("new-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        harness.AssertEarlierTransactionGone(applied.TransactionId);
        Assert.Equal(before, harness.SnapshotEarlierFolders());
        Assert.False(Directory.Exists(Path.Combine(harness.EarlierData, "app", "launcher-update")));
        Assert.False(Directory.Exists(Path.Combine(harness.EarlierData, "data")));
    }

    /// <summary>
    /// An earlier update whose plan cannot be read stops the start with the
    /// reason, and the rollback copy it left beside the launcher stays.
    /// Mutation: swallowing the unreadable plan fails this.
    /// </summary>
    [Fact]
    public async Task AnUnreadableEarlierUpdateStopsTheStartAndKeepsItsRollbackCopy()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        SelfUpdatePlan applied;
        using (harness.Earlier.Barrier.AcquireExclusive())
        {
            applied = await harness.Earlier.ApplyPendingAsync(harness.Target);
        }

        string rollbackCopy = harness.Earlier.GetTargetTransactionDirectory(applied);
        Assert.True(Directory.Exists(rollbackCopy));
        await File.WriteAllTextAsync(harness.Earlier.PendingPlanPath, "{ not a plan");

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            harness.Paths,
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            Settings(harness.Platform, TimeSpan.FromSeconds(5)));

        Assert.True(result.ShouldExit);
        Assert.Contains(harness.Earlier.PendingPlanPath, result.Refusal, StringComparison.Ordinal);
        Assert.True(Directory.Exists(rollbackCopy));
        Assert.False(Directory.Exists(harness.Paths.RootDirectory));
    }

    /// <summary>
    /// A start that knows nothing of an earlier update (its plan is in a
    /// data folder this start does not look in) leaves that update's rollback
    /// copy beside the launcher: this version only removes its own kind.
    /// Mutation: giving both layouts the same prefix fails this.
    /// </summary>
    [Fact]
    public async Task AnEarlierRollbackCopyIsNotThisVersionsToRemove()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        SelfUpdatePlan applied;
        using (harness.Earlier.Barrier.AcquireExclusive())
        {
            applied = await harness.Earlier.ApplyPendingAsync(harness.Target);
        }

        string rollbackCopy = harness.Earlier.GetTargetTransactionDirectory(applied);
        var elsewhere = new ProfileEnvironment(Path.Combine(_root, "another profile"));

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            ApplicationPathSet.Resolve(platform: elsewhere),
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            Settings(elsewhere, TimeSpan.FromSeconds(5)));

        Assert.False(result.ShouldExit);
        Assert.True(Directory.Exists(rollbackCopy));
    }

    /// <summary>
    /// A staged earlier update to a version this launcher already is (it was
    /// unzipped over the old one by hand) is dropped instead of run, which
    /// would take the launcher back. Mutation: running it regardless of the
    /// version fails this.
    /// </summary>
    [Fact]
    public async Task AStagedEarlierUpdateThisLauncherAlreadyHasIsDropped()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        SelfUpdatePlan staged = Assert.IsType<SelfUpdatePlan>(await harness.Earlier.LoadPendingAsync());
        var started = new List<System.Diagnostics.ProcessStartInfo>();

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            harness.Paths,
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            Settings(harness.Platform, TimeSpan.FromSeconds(5), started) with
            {
                CurrentVersion = LauncherVersion.Parse("2.0.0"),
            });

        Assert.False(result.ShouldExit);
        Assert.Empty(started);
        Assert.False(File.Exists(harness.Earlier.PendingPlanPath));
        Assert.False(Directory.Exists(harness.Earlier.GetTransactionDirectory(staged.TransactionId)));
        Assert.Equal("old-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
    }

    /// <summary>
    /// A normal start that undoes an interrupted earlier update puts the
    /// earlier launcher back and starts it, instead of carrying on as the
    /// version that was being put in. Mutation: carrying on fails this.
    /// </summary>
    [Fact]
    public async Task UndoingAnEarlierUpdateStartsTheRestoredLauncher()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        using (harness.Earlier.Barrier.AcquireExclusive())
        {
            _ = await harness.Earlier.ApplyPendingAsync(harness.Target);
            _ = await harness.Earlier.RollbackAwaitingConfirmationAsync(harness.Target);
        }

        var started = new List<System.Diagnostics.ProcessStartInfo>();

        SelfUpdateStartupResult result = await LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            harness.Paths,
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            Settings(harness.Platform, TimeSpan.FromSeconds(5), started));

        Assert.True(result.ShouldExit);
        Assert.Equal(0, result.ExitCode);
        System.Diagnostics.ProcessStartInfo restored = Assert.Single(started);
        Assert.Equal(harness.LauncherPath, restored.FileName);
        Assert.Equal(PublicArguments, restored.ArgumentList);
        Assert.Equal("old-launcher", await File.ReadAllTextAsync(harness.LauncherPath));
        Assert.False(File.Exists(harness.Earlier.PendingPlanPath));
        Assert.False(Directory.Exists(harness.Paths.RootDirectory));
    }

    /// <summary>
    /// A helper that keeps the lease past the wait does not stop the
    /// confirmed launcher: it starts with its arguments, and the next start
    /// completes the confirmed update in the earlier folder. Mutation:
    /// failing the start on the timeout fails this.
    /// </summary>
    [Fact]
    public async Task AConfirmerThatOutwaitsTheHelperStillStartsAndTheNextStartFinishes()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        UpdateSessionBarrier.ExclusiveLease helperLease = harness.Earlier.Barrier.AcquireExclusive();
        SelfUpdatePlan applied = await harness.Earlier.ApplyPendingAsync(harness.Target);

        SelfUpdateStartupResult confirmed;
        try
        {
            confirmed = await LauncherSelfUpdateBootstrap.HandleAsync(
                [LauncherSelfUpdateBootstrap.ConfirmArgument, applied.TransactionId, .. PublicArguments],
                harness.Paths,
                harness.Http,
                harness.Target,
                harness.LauncherPath,
                Settings(harness.Platform, TimeSpan.FromMilliseconds(300)));
        }
        finally
        {
            helperLease.Dispose();
        }

        Assert.False(confirmed.ShouldExit);
        Assert.Equal(PublicArguments, confirmed.RemainingArguments);
        Assert.True(harness.Earlier.IsConfirmed(applied.TransactionId));
        Assert.True(File.Exists(harness.Earlier.PendingPlanPath));

        SelfUpdateStartupResult next = await LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            harness.Paths,
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            Settings(harness.Platform, TimeSpan.FromSeconds(5)));

        Assert.False(next.ShouldExit);
        harness.AssertEarlierTransactionGone(applied.TransactionId);
    }

    /// <summary>
    /// A helper that is slow to exit leaves its copy in the earlier folder;
    /// the next start removes it. Mutation: not removing an earlier folder's
    /// leftovers when no plan is pending fails this.
    /// </summary>
    [Fact]
    public async Task TheHelpersCopyLeftByASlowExitIsRemovedByTheNextStart()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        UpdateSessionBarrier.ExclusiveLease helperLease = harness.Earlier.Barrier.AcquireExclusive();
        SelfUpdatePlan applied = await harness.Earlier.ApplyPendingAsync(harness.Target);
        string stagedLauncher = Path.Combine(
            harness.Earlier.GetPayloadDirectory(applied.TransactionId),
            harness.LauncherName);
        IDisposable running = RunningHelperCopy.Hold(stagedLauncher);
        Task helper = Task.Run(async () =>
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (!harness.Earlier.IsConfirmed(applied.TransactionId)
                   && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);
            await harness.Earlier.CompleteConfirmedAsync(applied.TransactionId, harness.Target);
            helperLease.Dispose();
        });

        try
        {
            SelfUpdateStartupResult confirmed = await LauncherSelfUpdateBootstrap.HandleAsync(
                [LauncherSelfUpdateBootstrap.ConfirmArgument, applied.TransactionId, .. PublicArguments],
                harness.Paths,
                harness.Http,
                harness.Target,
                harness.LauncherPath,
                Settings(harness.Platform, TimeSpan.FromSeconds(10)) with
                {
                    HelperExitWait = TimeSpan.FromMilliseconds(300),
                });
            await helper.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(confirmed.ShouldExit);
            Assert.True(Directory.Exists(harness.Earlier.GetTransactionDirectory(applied.TransactionId)));
        }
        finally
        {
            running.Dispose();
            helperLease.Dispose();
        }

        SelfUpdateStartupResult next = await LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            harness.Paths,
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            Settings(harness.Platform, TimeSpan.FromSeconds(5)));

        Assert.False(next.ShouldExit);
        Assert.False(Directory.Exists(harness.Earlier.GetTransactionDirectory(applied.TransactionId)));
    }

    /// <summary>
    /// The wait for the helper to exit starts when it lets the lease go, not
    /// when the confirmer started: a helper that is slow to finish still gets
    /// the full wait to exit, and its copy is removed. Mutation: one deadline
    /// for both waits fails this.
    /// </summary>
    [Fact]
    public async Task TheHelpersExitIsWaitedForOnItsOwnDeadline()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        UpdateSessionBarrier.ExclusiveLease helperLease = harness.Earlier.Barrier.AcquireExclusive();
        SelfUpdatePlan applied = await harness.Earlier.ApplyPendingAsync(harness.Target);
        string stagedLauncher = Path.Combine(
            harness.Earlier.GetPayloadDirectory(applied.TransactionId),
            harness.LauncherName);
        IDisposable running = RunningHelperCopy.Hold(stagedLauncher);
        Task helper = Task.Run(async () =>
        {
            try
            {
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (!harness.Earlier.IsConfirmed(applied.TransactionId)
                       && DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(20);

                // Slow to finish: the lease goes after 1 s, the process after 2 s more.
                await Task.Delay(TimeSpan.FromSeconds(1));
                await harness.Earlier.CompleteConfirmedAsync(applied.TransactionId, harness.Target);
                helperLease.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            finally
            {
                running.Dispose();
                helperLease.Dispose();
            }
        });

        SelfUpdateStartupResult confirmed = await LauncherSelfUpdateBootstrap.HandleAsync(
            [LauncherSelfUpdateBootstrap.ConfirmArgument, applied.TransactionId, .. PublicArguments],
            harness.Paths,
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            Settings(harness.Platform, TimeSpan.FromSeconds(10)) with
            {
                HelperLeaseWait = TimeSpan.FromSeconds(2),
            });
        await helper.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(confirmed.ShouldExit);
        harness.AssertEarlierTransactionGone(applied.TransactionId);
    }

    /// <summary>
    /// Starting the launcher by hand while its helper still waits for the
    /// confirmation is refused, and nothing starts on the new folder.
    /// Mutation: skipping the earlier update on a normal start fails this.
    /// </summary>
    [Fact]
    public async Task AHandStartWhileTheUpdateAwaitsConfirmationIsRefused()
    {
        using var harness = await EarlierHarness.StageAsync(_root);
        using UpdateSessionBarrier.ExclusiveLease helperLease = harness.Earlier.Barrier.AcquireExclusive();
        SelfUpdatePlan applied = await harness.Earlier.ApplyPendingAsync(harness.Target);

        await Assert.ThrowsAsync<LauncherUpdateException>(() => LauncherSelfUpdateBootstrap.HandleAsync(
            PublicArguments,
            harness.Paths,
            harness.Http,
            harness.Target,
            harness.LauncherPath,
            Settings(harness.Platform, TimeSpan.FromSeconds(5))));

        Assert.Equal(
            SelfUpdatePlanState.AwaitingConfirmation,
            Assert.IsType<SelfUpdatePlan>(await harness.Earlier.LoadPendingAsync()).State);
        Assert.False(harness.Earlier.IsConfirmed(applied.TransactionId));
        Assert.False(Directory.Exists(harness.Paths.RootDirectory));
    }

    /// <summary>
    /// The start's settings for a scratch profile: the given waits, a
    /// launcher older than the staged 2.0.0, and processes recorded instead
    /// of started.
    /// </summary>
    internal static SelfUpdateStartSettings Settings(
        IApplicationPathEnvironment platform,
        TimeSpan wait,
        List<System.Diagnostics.ProcessStartInfo>? started = null) => new()
    {
        Platform = platform,
        CurrentVersion = LauncherVersion.Parse("1.0.0"),
        HelperLeaseWait = wait,
        HelperExitWait = wait,
        StartProcess = start =>
        {
            started?.Add(start);
            return true;
        },
    };

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
            Assert.Empty(Directory.EnumerateDirectories(Target, Earlier.TargetTransactionPrefix + "*"));
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

        public static void Write(string folder, string relative, string content)
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

/// <summary>
/// Stands in for the helper process running from its staged copy: while held,
/// the copy cannot be removed. Windows refuses to delete a file that is open;
/// elsewhere an open file can be deleted, so the copy's folder is made
/// read-only instead, which refuses removing anything in it.
/// </summary>
internal sealed class RunningHelperCopy : IDisposable
{
    private readonly FileStream? _open;
    private readonly string? _folder;
    private readonly UnixFileMode _folderMode;

    private RunningHelperCopy(string executable)
    {
        if (OperatingSystem.IsWindows())
        {
            _open = File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            return;
        }

        _folder = Path.GetDirectoryName(executable)!;
        _folderMode = File.GetUnixFileMode(_folder);
        File.SetUnixFileMode(
            _folder,
            UnixFileMode.UserRead | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    public static IDisposable Hold(string executable) => new RunningHelperCopy(executable);

    public void Dispose()
    {
        _open?.Dispose();
        if (_folder is not null && Directory.Exists(_folder) && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_folder, _folderMode);
        }
    }
}
