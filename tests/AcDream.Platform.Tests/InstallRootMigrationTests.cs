using System.Text.Json.Nodes;
using AcDream.Platform;

namespace AcDream.Platform.Tests;

public sealed class InstallRootMigrationTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "openac-migration-" + Guid.NewGuid().ToString("N"));

    private readonly LegacyApplicationLayout _old;
    private readonly ApplicationPathSet _paths;

    public InstallRootMigrationTests()
    {
        string local = Path.Combine(_scratch, "Local", "acdream");
        _old = new LegacyApplicationLayout(
            Path.Combine(_scratch, "Roaming", "acdream"),
            local,
            Path.Combine(local, "cache"),
            local);
        _paths = ApplicationPathSet.ForRoot(
            Path.Combine(_scratch, "Local", "OpenAC"),
            ApplicationRootSource.Default);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
            Directory.Delete(_scratch, recursive: true);
    }

    /// <summary>
    /// Mutation: dropping any one mapping row, moving a not-migrated entry,
    /// or skipping the install record rewrite fails this.
    /// </summary>
    [Fact]
    public void EveryRowOfTheMappingLandsWhereTheLayoutSays()
    {
        SeedOldLayout();

        InstallRootMigrationResult result = InstallRootMigration.Run(_old, _paths);

        Assert.Equal(InstallRootMigrationOutcome.Migrated, result.Outcome);
        Assert.Empty(result.Failures);
        string root = _paths.RootDirectory;
        AssertFile(root, "app/0.1.15/AcDream.App.exe");
        AssertFile(root, "app/0.1.14/AcDream.App.exe");
        AssertFile(root, "app/current.json");
        AssertFile(root, "app/current.previous.json");
        AssertFile(root, "app/plugins-installed.json");
        AssertFile(root, "app/launcher-update/transactions/t.json");
        Assert.False(Directory.Exists(Path.Combine(root, "app", "0.1.10")));
        AssertFile(root, "data/pak/acdream.pak");
        AssertFile(root, "data/install.json");
        AssertFile(root, "data/install.verification.json");
        AssertFile(root, "settings/settings.json");
        AssertFile(root, "settings/keybinds.json");
        AssertFile(root, "settings/launcher-profiles.json");
        AssertFile(root, "settings/active-keymap.txt");
        AssertFile(root, "plugins/acdream.mosstank/plugin.json");
        AssertFile(root, "plugins/acdream.mosstank/files/state.json");
        AssertFile(root, "plugins/openac.goarrow/files/arrow.json");
        AssertFile(root, "vtank/mosstank/profiles/p.usd");
        AssertFile(root, "logs/chat.txt");
        AssertFile(root, "logs/crash/launcher-crash-1.log");
        AssertFile(root, "logs/crash/crash-20260911.json");
        Assert.False(Directory.Exists(Path.Combine(root, "navigation")));
        Assert.False(Directory.Exists(Path.Combine(root, "plugins-before-ub")));
        Assert.False(File.Exists(Path.Combine(root, "cache", "plugins.json")));
        Assert.False(Directory.Exists(Path.Combine(root, "plugin-peers")));

        // What was not brought over stays in the old folder.
        Assert.True(Directory.Exists(Path.Combine(_old.DataDirectory, "app", "0.1.10")));
        Assert.True(Directory.Exists(Path.Combine(_old.DataDirectory, "navigation")));
        Assert.True(Directory.Exists(Path.Combine(_old.DataDirectory, "plugin-backups")));
        Assert.True(File.Exists(Path.Combine(_old.DataDirectory, ".install.lock")));
        Assert.True(File.Exists(Path.Combine(_old.CacheDirectory, "plugins.json")));

        JsonObject install = ReadObject(Path.Combine(root, "data", "install.json"));
        Assert.Equal(
            Path.Combine(root, "data", "pak", "acdream.pak"),
            install["preparedAssetPath"]!.GetValue<string>());
        Assert.Equal(
            "C:\\Games\\AC",
            install["datDirectory"]!.GetValue<string>());
        JsonObject verification = ReadObject(
            Path.Combine(root, "data", "install.verification.json"));
        Assert.Equal(
            Path.Combine(root, "data", "pak", "acdream.pak"),
            verification["path"]!.GetValue<string>());

        Assert.True(File.Exists(Path.Combine(_old.DataDirectory, InstallRootMigration.MovedNoteFileName)));
        Assert.True(File.Exists(Path.Combine(_old.ConfigDirectory, InstallRootMigration.MovedNoteFileName)));
        Assert.True(File.Exists(_paths.LayoutMarkerFile));
    }

    /// <summary>Mutation: writing the marker before the moves, or not checking it, fails this.</summary>
    [Fact]
    public void SecondRunDoesNothing()
    {
        SeedOldLayout();
        InstallRootMigration.Run(_old, _paths);
        File.WriteAllText(Path.Combine(_old.ConfigDirectory, "settings.json"), "{\"late\":true}");

        InstallRootMigrationResult second = InstallRootMigration.Run(_old, _paths);

        Assert.Equal(InstallRootMigrationOutcome.AlreadyDone, second.Outcome);
        Assert.True(File.Exists(Path.Combine(_old.ConfigDirectory, "settings.json")));
    }

    /// <summary>
    /// Mutation: a planner that asks for sources that are already gone, or an
    /// executor that stops at the first missing source, fails this.
    /// </summary>
    [Fact]
    public void HalfDoneRunResumesFromWhatIsLeft()
    {
        SeedOldLayout();
        // An earlier run moved the logs and the pak and was stopped before
        // the install record, the settings or the marker.
        Directory.CreateDirectory(_paths.RootDirectory);
        Directory.Move(Path.Combine(_old.DataDirectory, "logs"), _paths.LogsDirectory);
        Directory.CreateDirectory(_paths.GameDataDirectory);
        Directory.Move(
            Path.Combine(_old.DataDirectory, "pak"),
            Path.Combine(_paths.GameDataDirectory, "pak"));

        InstallRootMigrationResult result = InstallRootMigration.Run(_old, _paths);

        Assert.Equal(InstallRootMigrationOutcome.Migrated, result.Outcome);
        AssertFile(_paths.RootDirectory, "logs/chat.txt");
        AssertFile(_paths.RootDirectory, "data/pak/acdream.pak");
        AssertFile(_paths.RootDirectory, "settings/settings.json");
        Assert.Empty(InstallRootMigration.Plan(_old, _paths).Steps);
    }

    /// <summary>
    /// Mutation: dropping the rewrite step when the record was already moved
    /// fails this (the launcher would then refuse the install).
    /// </summary>
    [Fact]
    public void RunStoppedBetweenMoveAndRewriteStillRewritesTheRecord()
    {
        Directory.CreateDirectory(_paths.GameDataDirectory);
        File.WriteAllText(
            Path.Combine(_paths.GameDataDirectory, "install.json"),
            "{ \"preparedAssetPath\": \"C:\\\\old\\\\pak\\\\acdream.pak\", \"version\": 1 }");

        InstallRootMigrationResult result = InstallRootMigration.Run(_old, _paths);

        Assert.Equal(InstallRootMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(
            Path.Combine(_paths.GameDataDirectory, "pak", "acdream.pak"),
            ReadObject(Path.Combine(_paths.GameDataDirectory, "install.json"))["preparedAssetPath"]!
                .GetValue<string>());
    }

    /// <summary>
    /// Mutation: making the copy path skip the source delete, or copy without
    /// the rename into place, fails this.
    /// </summary>
    [Fact]
    public void AcrossVolumesEverythingIsCopiedThenDeleted()
    {
        SeedOldLayout();

        InstallRootMigrationResult result = InstallRootMigration.Run(
            _old,
            _paths,
            sameVolume: static (_, _) => false);

        Assert.Equal(InstallRootMigrationOutcome.Migrated, result.Outcome);
        AssertFile(_paths.RootDirectory, "app/0.1.15/AcDream.App.exe");
        AssertFile(_paths.RootDirectory, "plugins/acdream.mosstank/files/state.json");
        AssertFile(_paths.RootDirectory, "vtank/mosstank/profiles/p.usd");
        Assert.False(Directory.Exists(Path.Combine(_old.DataDirectory, "vtank")));
        Assert.False(Directory.Exists(Path.Combine(_old.DataDirectory, "app", "0.1.15")));
        Assert.False(File.Exists(Path.Combine(_old.ConfigDirectory, "settings.json")));
        Assert.Empty(Directory.EnumerateFiles(_paths.RootDirectory, "*.moving", SearchOption.AllDirectories));
    }

    /// <summary>
    /// A file the new root already has is a conflict, and neither copy is
    /// lost: the new one stays, the old one lands beside it as .from-old, and
    /// the run reports it (and the marker records it). Mutation: skipping a
    /// conflicting file (leaving it in the old folder that "Remove the old
    /// folders" later deletes) or overwriting the new one fails this.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AConflictKeepsBothCopiesAndIsReported(bool sameVolume)
    {
        SeedOldLayout();
        File.WriteAllText(Path.Combine(_old.ConfigDirectory, "settings.json"), "{\"old\":true}");
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(_paths.SettingsFile, "{\"new\":true}");

        InstallRootMigrationResult result = InstallRootMigration.Run(
            _old,
            _paths,
            (_, _) => sameVolume);

        string kept = _paths.SettingsFile + ".from-old";
        Assert.Equal(InstallRootMigrationOutcome.Migrated, result.Outcome);
        Assert.Equal("{\"new\":true}", File.ReadAllText(_paths.SettingsFile));
        Assert.Equal("{\"old\":true}", File.ReadAllText(kept));
        Assert.False(File.Exists(Path.Combine(_old.ConfigDirectory, "settings.json")));
        Assert.Equal([kept], result.Conflicts);
        Assert.Contains(
            kept,
            File.ReadAllText(_paths.LayoutMarkerFile).Replace("\\\\", "\\"),
            StringComparison.Ordinal);
    }

    /// <summary>Mutation: reusing an existing .from-old name fails this.</summary>
    [Fact]
    public void ASecondConflictTakesTheNextFreeName()
    {
        string folder = Path.Combine(_scratch, "conflict");
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, "a.json");
        File.WriteAllText(destination, "new");
        File.WriteAllText(destination + ".from-old", "earlier");
        string source = Path.Combine(_scratch, "a.json");
        File.WriteAllText(source, "old");
        var failures = new List<string>();
        var conflicts = new List<string>();

        Assert.True(InstallRootFileMover.MoveTree(source, destination, (_, _) => true, failures, conflicts));

        Assert.Equal([destination + ".from-old-2"], conflicts);
        Assert.Equal("old", File.ReadAllText(destination + ".from-old-2"));
        Assert.Equal("earlier", File.ReadAllText(destination + ".from-old"));
        Assert.Empty(failures);
    }

    /// <summary>
    /// Conflicts of an interrupted run are still reported by the run that
    /// finishes. Mutation: dropping the pending conflicts file fails this.
    /// </summary>
    [Fact]
    public void ConflictsOfAnInterruptedRunReachTheRunThatFinishes()
    {
        SeedOldLayout();
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(_paths.SettingsFile, "{\"new\":true}");
        File.WriteAllText(_paths.LogsDirectory, "in the way");

        InstallRootMigrationResult first = InstallRootMigration.Run(_old, _paths);
        File.Delete(_paths.LogsDirectory);
        InstallRootMigrationResult second = InstallRootMigration.Run(_old, _paths);

        Assert.Equal(InstallRootMigrationOutcome.Incomplete, first.Outcome);
        Assert.Equal(InstallRootMigrationOutcome.Migrated, second.Outcome);
        Assert.Equal([_paths.SettingsFile + ".from-old"], second.Conflicts);
    }

    /// <summary>Mutation: writing the marker despite a failed step fails this.</summary>
    [Fact]
    public void AFailedStepLeavesNoMarkerSoTheNextStartResumes()
    {
        SeedOldLayout();
        Directory.CreateDirectory(_paths.RootDirectory);
        // A file where the logs folder must go makes that one step fail.
        File.WriteAllText(_paths.LogsDirectory, "in the way");

        InstallRootMigrationResult result = InstallRootMigration.Run(_old, _paths);

        Assert.Equal(InstallRootMigrationOutcome.Incomplete, result.Outcome);
        Assert.NotEmpty(result.Failures);
        Assert.False(File.Exists(_paths.LayoutMarkerFile));
        AssertFile(_paths.RootDirectory, "settings/settings.json");
    }

    /// <summary>Mutation: migrating into an explicitly named root fails this.</summary>
    [Fact]
    public void AnExplicitlyNamedRootIsNeverMigratedInto()
    {
        ApplicationPathSet explicitRoot = ApplicationPathSet.ForRoot(
            Path.Combine(_scratch, "isolated"));

        InstallRootMigrationResult result = InstallRootMigration.RunIfNeeded(explicitRoot);

        Assert.Equal(InstallRootMigrationOutcome.NotApplicable, result.Outcome);
        Assert.False(Directory.Exists(explicitRoot.RootDirectory));
    }

    /// <summary>Mutation: not marking a fresh root fails this.</summary>
    [Fact]
    public void WithoutAnOldLayoutTheRootIsMarkedFresh()
    {
        InstallRootMigrationResult result = InstallRootMigration.Run(_old, _paths);

        Assert.Equal(InstallRootMigrationOutcome.Fresh, result.Outcome);
        Assert.True(File.Exists(_paths.LayoutMarkerFile));
        Assert.Empty(InstallRootMigration.ReadOldRoots(_paths));
    }

    /// <summary>Mutation: ignoring the earlier settings folder fails this.</summary>
    [Fact]
    public void SettingsOnlyTheEarlierFolderHadAreBroughtOver()
    {
        Directory.CreateDirectory(_old.EarlierConfigDirectory!);
        File.WriteAllText(Path.Combine(_old.EarlierConfigDirectory!, "keybinds.json"), "{}");

        InstallRootMigration.Run(_old, _paths);

        AssertFile(_paths.RootDirectory, "settings/keybinds.json");
    }

    /// <summary>
    /// Only what the migration left behind is deleted, and only from old
    /// folders whose note names this install. Mutation: deleting a root whose
    /// note names another install, or leaving a listed entry, fails this.
    /// </summary>
    [Fact]
    public void RemovalDeletesWhatTheMigrationLeftBehindInNotedFolders()
    {
        SeedOldLayout();
        InstallRootMigration.Run(_old, _paths);
        File.WriteAllText(
            Path.Combine(_old.ConfigDirectory, InstallRootMigration.MovedNoteFileName),
            "somewhere else");

        OldRootRemovalPlan plan = OldRootRemoval.Plan(_paths);
        IReadOnlyList<string> failures = OldRootRemoval.Remove(_paths);

        Assert.True(plan.CanRemove);
        Assert.Contains(plan.Items, item => item.Path == Path.Combine(_old.DataDirectory, "plugin-backups"));
        Assert.Contains(plan.Items, item => item.Path == Path.Combine(_old.DataDirectory, "app") && item.Bytes > 0);
        Assert.Empty(failures);
        Assert.False(Directory.Exists(_old.DataDirectory));
        Assert.True(Directory.Exists(_old.ConfigDirectory));
    }

    /// <summary>
    /// Something written into an old folder after the move (an old client
    /// still running from it) stops the removal entirely. Mutation: deleting
    /// entries that were not recorded as left behind fails this.
    /// </summary>
    [Fact]
    public void RemovalRefusesWhenSomethingAppearedAfterTheMove()
    {
        SeedOldLayout();
        InstallRootMigration.Run(_old, _paths);
        Directory.CreateDirectory(Path.Combine(_old.DataDirectory, "crash-reports"));
        File.WriteAllText(Path.Combine(_old.DataDirectory, "crash-reports", "late.log"), "x");
        // Dated before the move, so only the not-left-behind rule can catch it.
        File.SetLastWriteTimeUtc(
            Path.Combine(_old.DataDirectory, "crash-reports", "late.log"),
            new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        IReadOnlyList<string> refusals = OldRootRemoval.Remove(_paths);

        Assert.NotEmpty(refusals);
        Assert.True(File.Exists(Path.Combine(_old.DataDirectory, "crash-reports", "late.log")));
        Assert.True(Directory.Exists(Path.Combine(_old.DataDirectory, "plugin-backups")));
    }

    /// <summary>Mutation: dropping the newer-than-the-move check fails this.</summary>
    [Fact]
    public void RemovalRefusesWhenALeftBehindFileChangedAfterTheMove()
    {
        SeedOldLayout();
        InstallRootMigration.Run(_old, _paths);
        string touched = Path.Combine(_old.DataDirectory, "app", "0.1.10", "AcDream.App.exe");
        File.SetLastWriteTimeUtc(
            touched,
            File.GetLastWriteTimeUtc(_paths.LayoutMarkerFile).AddMinutes(1));

        OldRootRemovalPlan plan = OldRootRemoval.Plan(_paths);

        Assert.False(plan.CanRemove);
        Assert.Contains(plan.Refusals, refusal => refusal.Contains(touched, StringComparison.Ordinal));
    }

    /// <summary>Mutation: formatting sizes with the current culture fails this under sv-SE.</summary>
    [Fact]
    public void SizesReadTheSameInEveryLocale()
    {
        Assert.Equal("512 B", OldRootRemovalItem.FormatBytes(512));
        Assert.Equal("1.5 KB", OldRootRemovalItem.FormatBytes(1536));
        Assert.Equal("2.0 GB", OldRootRemovalItem.FormatBytes(2L * 1024 * 1024 * 1024));
    }

    /// <summary>
    /// A second process finding the migration in progress waits for it, then
    /// gives up as Busy rather than running on a half-moved root.
    /// Mutation: returning Busy without waiting fails this.
    /// </summary>
    [Fact]
    public void ABusyMigrationIsWaitedForThenRefused()
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        using (new FileStream(
                   Path.Combine(_paths.RootDirectory, ".migration.lock"),
                   FileMode.OpenOrCreate,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            InstallRootMigrationResult result = InstallRootMigration.RunWaitingForOthers(
                _old,
                _paths,
                sameVolume: null,
                TimeSpan.FromMilliseconds(600));

            Assert.Equal(InstallRootMigrationOutcome.Busy, result.Outcome);
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(500), clock.Elapsed.ToString());
            Assert.NotNull(InstallRootMigration.StartupBlockReason(result, _paths.RootDirectory));
        }
    }

    /// <summary>Mutation: giving up at the first Busy fails this.</summary>
    [Fact]
    public async Task AMigrationThatFinishesWhileWaitingLetsTheWaiterProceed()
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        var holder = new FileStream(
            Path.Combine(_paths.RootDirectory, ".migration.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        Task release = Task.Delay(300).ContinueWith(_ => holder.Dispose(), TaskScheduler.Default);

        InstallRootMigrationResult result = InstallRootMigration.RunWaitingForOthers(
            _old,
            _paths,
            sameVolume: null,
            TimeSpan.FromSeconds(10));
        await release;

        Assert.Equal(InstallRootMigrationOutcome.Fresh, result.Outcome);
        Assert.Null(InstallRootMigration.StartupBlockReason(result, _paths.RootDirectory));
    }

    /// <summary>Mutation: letting an incomplete migration start sessions fails this.</summary>
    [Fact]
    public void AnIncompleteMigrationBlocksStartup()
    {
        SeedOldLayout();
        Directory.CreateDirectory(_paths.RootDirectory);
        File.WriteAllText(_paths.LogsDirectory, "in the way");

        InstallRootMigrationResult result = InstallRootMigration.Run(_old, _paths);

        Assert.Contains("retry", InstallRootMigration.StartupBlockReason(result, _paths.RootDirectory), StringComparison.Ordinal);
    }

    /// <summary>
    /// A record still naming another root's content is pointed at this root's,
    /// once the content is here; a record naming a path inside this root, or
    /// content that is not here, is left alone. Mutation: rewriting whenever
    /// the path differs, or never, fails this.
    /// </summary>
    [Fact]
    public void RepairPointsMovedRecordsAtThisRootsContentOnly()
    {
        string package = Path.Combine(_paths.GameDataDirectory, "pak", "acdream.pak");
        string record = Path.Combine(_paths.GameDataDirectory, "install.json");
        Directory.CreateDirectory(_paths.GameDataDirectory);
        string elsewhere = Path.Combine(_scratch, "Elsewhere", "OpenAC", "data", "pak", "acdream.pak");
        File.WriteAllText(record, "{ \"preparedAssetPath\": " + System.Text.Json.JsonSerializer.Serialize(elsewhere) + " }");

        Assert.Empty(InstallRootMigration.RepairContentRecords(_paths));

        Write(_paths.GameDataDirectory, "pak/acdream.pak", "pak");
        Assert.Equal([record], InstallRootMigration.RepairContentRecords(_paths));
        Assert.Equal(package, ReadObject(record)["preparedAssetPath"]!.GetValue<string>());
        Assert.Empty(InstallRootMigration.RepairContentRecords(_paths));

        string inside = Path.Combine(_paths.GameDataDirectory, "custom.pak");
        File.WriteAllText(record, "{ \"preparedAssetPath\": " + System.Text.Json.JsonSerializer.Serialize(inside) + " }");
        Assert.Empty(InstallRootMigration.RepairContentRecords(_paths));
    }

    private void SeedOldLayout()
    {
        string data = _old.DataDirectory;
        string config = _old.ConfigDirectory;
        Write(data, "app/0.1.15/AcDream.App.exe", "x");
        Write(data, "app/0.1.14/AcDream.App.exe", "x");
        Write(data, "app/0.1.10/AcDream.App.exe", "x");
        Write(data, "app/current.json", "{ \"schemaVersion\": 1, \"currentVersion\": \"0.1.15\", \"previousVersion\": \"0.1.14\" }");
        Write(data, "app/current.previous.json", "{}");
        Write(data, "app/plugins-installed.json", "{}");
        Write(data, "app/.update-session.lock", string.Empty);
        Write(data, "launcher-update/transactions/t.json", "{}");
        Write(data, "pak/acdream.pak", "pak");
        Write(
            data,
            "install.json",
            "{ \"datDirectory\": \"C:\\\\Games\\\\AC\", \"preparedAssetPath\": "
            + System.Text.Json.JsonSerializer.Serialize(Path.Combine(data, "pak", "acdream.pak"))
            + ", \"version\": 1 }");
        Write(
            data,
            "install.verification.json",
            "{ \"version\": 1, \"path\": "
            + System.Text.Json.JsonSerializer.Serialize(Path.Combine(data, "pak", "acdream.pak"))
            + " }");
        Write(data, ".install.lock", string.Empty);
        Write(data, "plugins/acdream.mosstank/plugin.json", "{}");
        Write(data, "vtank/mosstank/profiles/p.usd", "u");
        Write(data, "logs/chat.txt", "hi");
        Write(data, "crash-reports/launcher-crash-1.log", "boom");
        Write(data, "navigation/x.nav", "n");
        Write(data, "plugins-before-ub/a/plugin.json", "{}");
        Write(data, "plugins-disabled/b/plugin.json", "{}");
        Write(data, "plugin-backups/c.zip", "z");
        Write(data, "plugin-peers/peer.json", "{}");
        Write(_old.CacheDirectory, "plugins.json", "{}");
        Write(_old.CacheDirectory, "diagnostics/crash-20260911.json", "{}");
        Write(_old.CacheDirectory, "diagnostics/graphical-capabilities-vulkan.json", "{}");
        Write(config, "settings.json", "{}");
        Write(config, "keybinds.json", "{}");
        Write(config, "launcher-profiles.json", "{}");
        Write(config, "active-keymap.txt", "default");
        Write(config, "plugins/acdream.mosstank/state.json", "{}");
        Write(config, "plugins/openac.goarrow/arrow.json", "{}");
    }

    private static void Write(string root, string relative, string content)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void AssertFile(string root, string relative) =>
        Assert.True(
            File.Exists(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))),
            $"missing {relative}");

    private static JsonObject ReadObject(string path) =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
}
