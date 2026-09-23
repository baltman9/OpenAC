using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcDream.Platform;

/// <summary>
/// Where the layout before the single install root kept things: separate
/// settings, data and cache folders named <c>acdream</c> in the operating
/// system's per-user locations.
/// </summary>
/// <param name="ConfigDirectory">Settings, key bindings, launcher profiles, plugin storage.</param>
/// <param name="DataDirectory">Client versions, prepared content, plugins, logs, profiles.</param>
/// <param name="CacheDirectory">Rebuildable files and client crash reports.</param>
/// <param name="EarlierConfigDirectory">
/// Where settings lived before they had their own folder, read only for the
/// settings and key binding files the config folder lacks.
/// </param>
public sealed record LegacyApplicationLayout(
    string ConfigDirectory,
    string DataDirectory,
    string CacheDirectory,
    string? EarlierConfigDirectory)
{
    /// <summary>The per-user folders the old layout used on this operating system.</summary>
    public static LegacyApplicationLayout Detect(
        IApplicationPathEnvironment platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (platform.IsWindows)
        {
            string roaming = ApplicationPathSet.RequireFolder(
                platform,
                Environment.SpecialFolder.ApplicationData);
            string local = ApplicationPathSet.RequireFolder(
                platform,
                Environment.SpecialFolder.LocalApplicationData);
            return new LegacyApplicationLayout(
                Path.Combine(roaming, "acdream"),
                Path.Combine(local, "acdream"),
                Path.Combine(local, "acdream", "cache"),
                Path.Combine(local, "acdream"));
        }

        string home = ApplicationPathSet.RequireFolder(
            platform,
            Environment.SpecialFolder.UserProfile);
        if (platform.IsMacOS)
        {
            string data = Path.Combine(
                home,
                "Library",
                "Application Support",
                "acdream");
            return new LegacyApplicationLayout(
                Path.Combine(data, "config"),
                data,
                Path.Combine(home, "Library", "Caches", "acdream"),
                ApplicationPathSet.ResolveXdg(
                    platform,
                    "XDG_CONFIG_HOME",
                    Path.Combine(home, ".config"),
                    "acdream"));
        }

        return new LegacyApplicationLayout(
            ApplicationPathSet.ResolveXdg(
                platform,
                "XDG_CONFIG_HOME",
                Path.Combine(home, ".config"),
                "acdream"),
            ApplicationPathSet.ResolveXdg(
                platform,
                "XDG_DATA_HOME",
                Path.Combine(home, ".local", "share"),
                "acdream"),
            ApplicationPathSet.ResolveXdg(
                platform,
                "XDG_CACHE_HOME",
                Path.Combine(home, ".cache"),
                "acdream"),
            EarlierConfigDirectory: null);
    }

    /// <summary>
    /// The top-level folders that get the note naming the new root: every old
    /// folder that is not inside another one.
    /// </summary>
    public IReadOnlyList<string> TopLevelRoots()
    {
        string[] candidates = [ConfigDirectory, DataDirectory, CacheDirectory];
        var roots = new List<string>();
        foreach (string candidate in candidates)
        {
            bool nested = candidates.Any(other =>
                !ApplicationPathIdentity.Equals(other, candidate)
                && ApplicationPathIdentity.IsSameOrInside(candidate, other));
            if (!nested && !roots.Any(root => ApplicationPathIdentity.Equals(root, candidate)))
                roots.Add(candidate);
        }

        return roots;
    }
}

/// <summary>What one migration step does.</summary>
public enum InstallRootMigrationStepKind
{
    /// <summary>Moves a file or folder, merging into a folder that already exists.</summary>
    Move,

    /// <summary>Points a moved install record at the moved prepared content.</summary>
    RewriteInstallRecord,

    /// <summary>Points a moved verification record at the moved prepared content.</summary>
    RewriteVerificationRecord,
}

/// <summary>One step of a migration plan.</summary>
/// <param name="Kind">What the step does.</param>
/// <param name="Source">The old file or folder, or the record to rewrite.</param>
/// <param name="Destination">The new file or folder, or the new content path a record must name.</param>
public sealed record InstallRootMigrationStep(
    InstallRootMigrationStepKind Kind,
    string Source,
    string Destination);

/// <summary>Everything a migration will do, decided before anything is touched.</summary>
public sealed record InstallRootMigrationPlan(
    IReadOnlyList<InstallRootMigrationStep> Steps,
    IReadOnlyList<string> OldRoots)
{
    /// <summary>True when the old layout had nothing to bring over.</summary>
    public bool IsEmpty => Steps.Count == 0;
}

/// <summary>How a migration run ended.</summary>
public enum InstallRootMigrationOutcome
{
    /// <summary>The root was named explicitly; the old layout is not this process's business.</summary>
    NotApplicable,

    /// <summary>The root already carries its layout marker.</summary>
    AlreadyDone,

    /// <summary>Another process is migrating the same root right now.</summary>
    Busy,

    /// <summary>There was no old layout; the root was marked as a fresh one.</summary>
    Fresh,

    /// <summary>Everything in the plan was moved and the marker was written.</summary>
    Migrated,

    /// <summary>Some steps failed; the marker was not written, so the next start resumes.</summary>
    Incomplete,
}

/// <summary>The result of one migration run.</summary>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Moved">The destinations that were moved in full.</param>
/// <param name="Failures">One line per entry that could not be moved.</param>
/// <param name="Warnings">Things that did not stop the migration, such as a note that could not be written.</param>
public sealed record InstallRootMigrationResult(
    InstallRootMigrationOutcome Outcome,
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Old files whose name the new root already had: each was kept beside
    /// the new one as <c>&lt;name&gt;.from-old</c>, so neither copy is lost. A run
    /// that finishes an earlier interrupted one reports that run's too.
    /// </summary>
    public IReadOnlyList<string> Conflicts { get; init; } = [];

    internal static InstallRootMigrationResult Of(InstallRootMigrationOutcome outcome) =>
        new(outcome, [], [], []);
}

/// <summary>
/// Brings the old per-user layout into the single install root, once. The
/// planner decides everything from what is on disk without touching it; the
/// executor moves what the plan names (a rename on the same volume, a copy
/// then delete across volumes), rewrites the install record's content path,
/// leaves a note in each old folder, and writes the root's layout marker last,
/// so an interrupted run resumes from what is left.
/// </summary>
public static class InstallRootMigration
{
    /// <summary>The note left in every old folder.</summary>
    public const string MovedNoteFileName = "MOVED-TO-OpenAC.txt";

    private const string LockFileName = ".migration.lock";

    private static readonly string[] EarlierSettingsFiles =
    [
        "settings.json",
        "keybinds.json",
    ];

    /// <summary>
    /// Runs the migration when <paramref name="paths"/> came from the pointer
    /// or the default and the root has no layout marker yet. An explicitly
    /// named root (a launcher argument, the launcher's environment for its
    /// children, a bot configuration, automation isolation) is left alone.
    /// </summary>
    /// <param name="sameVolume">
    /// Whether two paths share a volume, so a rename can move between them.
    /// Tests pass <c>(_, _) =&gt; false</c> to drive the copy path.
    /// </param>
    /// <param name="busyWait">
    /// How long to wait for another process that is migrating the same root
    /// before giving up with <see cref="InstallRootMigrationOutcome.Busy"/>;
    /// <see cref="DefaultBusyWait"/> when omitted.
    /// </param>
    public static InstallRootMigrationResult RunIfNeeded(
        ApplicationPathSet paths,
        IApplicationPathEnvironment? platform = null,
        Func<string, string, bool>? sameVolume = null,
        TimeSpan? busyWait = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        platform ??= ApplicationPathEnvironment.Instance;
        if (paths.RootSource is not (ApplicationRootSource.Pointer
            or ApplicationRootSource.Default))
        {
            return InstallRootMigrationResult.Of(
                InstallRootMigrationOutcome.NotApplicable);
        }

        return RunWaitingForOthers(
            LegacyApplicationLayout.Detect(platform),
            paths,
            sameVolume,
            busyWait ?? DefaultBusyWait);
    }

    /// <summary>How long a starting process waits for another one that is migrating.</summary>
    public static readonly TimeSpan DefaultBusyWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs the migration, waiting up to <paramref name="busyWait"/> while
    /// another process holds the migration lock: when that one finishes, this
    /// run finds the marker and has nothing to do.
    /// </summary>
    public static InstallRootMigrationResult RunWaitingForOthers(
        LegacyApplicationLayout old,
        ApplicationPathSet paths,
        Func<string, string, bool>? sameVolume,
        TimeSpan busyWait)
    {
        DateTime deadline = DateTime.UtcNow + busyWait;
        while (true)
        {
            InstallRootMigrationResult result = Run(old, paths, sameVolume);
            if (result.Outcome != InstallRootMigrationOutcome.Busy
                || DateTime.UtcNow >= deadline)
            {
                return result;
            }

            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// Why nothing may start against the root after this migration run, or
    /// null. A half-moved install, or one another process is still moving,
    /// would have a client read settings and content from two places.
    /// </summary>
    public static string? StartupBlockReason(InstallRootMigrationResult result, string root)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            InstallRootMigrationOutcome.Incomplete =>
                $"Moving the old OpenAC folders into {root} is not finished "
                + $"({result.Failures.Count} item(s) were in use). Close every OpenAC client, "
                + "bot and older launcher, then retry.",
            InstallRootMigrationOutcome.Busy =>
                $"Another OpenAC program is still moving the old folders into {root}. "
                + "Wait for it to finish, then start again.",
            _ => null,
        };
    }

    /// <summary>Runs the migration from <paramref name="old"/> into <paramref name="paths"/>.</summary>
    public static InstallRootMigrationResult Run(
        LegacyApplicationLayout old,
        ApplicationPathSet paths,
        Func<string, string, bool>? sameVolume = null)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(paths);
        if (File.Exists(paths.LayoutMarkerFile))
        {
            return InstallRootMigrationResult.Of(
                InstallRootMigrationOutcome.AlreadyDone);
        }

        Directory.CreateDirectory(paths.RootDirectory);
        FileStream? migrationLock;
        try
        {
            migrationLock = new FileStream(
                Path.Combine(paths.RootDirectory, LockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return InstallRootMigrationResult.Of(InstallRootMigrationOutcome.Busy);
        }

        using (migrationLock)
        {
            // Asked again under the lock: another process may have finished
            // while this one waited for it.
            if (File.Exists(paths.LayoutMarkerFile))
            {
                return InstallRootMigrationResult.Of(
                    InstallRootMigrationOutcome.AlreadyDone);
            }

            InstallRootMigrationPlan plan = Plan(old, paths);
            if (plan.IsEmpty)
            {
                WriteLayoutMarker(paths, migratedFrom: []);
                return InstallRootMigrationResult.Of(
                    InstallRootMigrationOutcome.Fresh);
            }

            (IReadOnlyList<string> moved, IReadOnlyList<string> failures, IReadOnlyList<string> newConflicts) =
                Execute(plan, sameVolume ?? SameVolume);
            IReadOnlyList<string> conflicts = [.. ReadPendingConflicts(paths), .. newConflicts];
            var warnings = new List<string>();
            foreach (string oldRoot in plan.OldRoots)
            {
                if (TryWriteMovedNote(oldRoot, paths.RootDirectory) is { } warning)
                    warnings.Add(warning);
            }

            if (failures.Count > 0)
            {
                // Kept for the run that finishes, so the conflicts of every
                // run reach the player once.
                WritePendingConflicts(paths, conflicts);
                return new InstallRootMigrationResult(
                    InstallRootMigrationOutcome.Incomplete,
                    moved,
                    failures,
                    warnings)
                {
                    Conflicts = conflicts,
                };
            }

            WriteLayoutMarker(
                paths,
                plan.OldRoots,
                conflicts,
                OldRootRemoval.SnapshotLeftBehind(plan.OldRoots));
            File.Delete(PendingConflictsPath(paths));
            return new InstallRootMigrationResult(
                InstallRootMigrationOutcome.Migrated,
                moved,
                failures,
                warnings)
            {
                Conflicts = conflicts,
            };
        }
    }

    /// <summary>
    /// Decides every step from what is on disk, without changing anything.
    /// A source that no longer exists produces no step, which is what lets a
    /// half-finished run resume.
    /// </summary>
    public static InstallRootMigrationPlan Plan(
        LegacyApplicationLayout old,
        ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(paths);
        var steps = new List<InstallRootMigrationStep>();

        void Move(string source, string destination)
        {
            if (File.Exists(source) || Directory.Exists(source))
            {
                steps.Add(new InstallRootMigrationStep(
                    InstallRootMigrationStepKind.Move,
                    source,
                    destination));
            }
        }

        // The old folders can coincide with the new root only when a caller
        // pointed the new root at an old folder on purpose; moving a folder
        // into itself would be nonsense, so such a root is left as it is.
        bool TouchesNewRoot(string oldFolder) =>
            ApplicationPathIdentity.IsSameOrInside(paths.RootDirectory, oldFolder)
            || ApplicationPathIdentity.IsSameOrInside(oldFolder, paths.RootDirectory);

        if (TouchesNewRoot(old.DataDirectory)
            || TouchesNewRoot(old.ConfigDirectory)
            || TouchesNewRoot(old.CacheDirectory))
        {
            return new InstallRootMigrationPlan([], []);
        }

        // Client versions: only the current and previous ones come along.
        string oldApp = Path.Combine(old.DataDirectory, "app");
        if (Directory.Exists(oldApp))
        {
            foreach (string version in ReadKeptClientVersions(oldApp))
            {
                Move(
                    Path.Combine(oldApp, version),
                    Path.Combine(paths.AppDirectory, version));
            }

            foreach (string file in Directory.EnumerateFiles(oldApp, "current*.json")
                         .OrderBy(static file => file, StringComparer.Ordinal))
            {
                Move(file, Path.Combine(paths.AppDirectory, Path.GetFileName(file)));
            }

            Move(
                Path.Combine(oldApp, "plugins-installed.json"),
                Path.Combine(paths.AppDirectory, "plugins-installed.json"));
        }

        Move(
            Path.Combine(old.DataDirectory, "launcher-update"),
            Path.Combine(paths.AppDirectory, "launcher-update"));

        // Prepared content and the records that name it.
        string oldPak = Path.Combine(old.DataDirectory, "pak");
        string newPak = Path.Combine(paths.GameDataDirectory, "pak");
        Move(oldPak, newPak);
        string oldInstallRecord = Path.Combine(old.DataDirectory, "install.json");
        string newInstallRecord = Path.Combine(paths.GameDataDirectory, "install.json");
        Move(oldInstallRecord, newInstallRecord);
        string oldVerification = Path.Combine(
            old.DataDirectory,
            "install.verification.json");
        string newVerification = Path.Combine(
            paths.GameDataDirectory,
            "install.verification.json");
        Move(oldVerification, newVerification);
        string newPackage = Path.Combine(newPak, "acdream.pak");
        if (File.Exists(oldInstallRecord)
            || NamesOtherPath(newInstallRecord, "preparedAssetPath", newPackage))
        {
            steps.Add(new InstallRootMigrationStep(
                InstallRootMigrationStepKind.RewriteInstallRecord,
                newInstallRecord,
                newPackage));
        }

        if (File.Exists(oldVerification)
            || NamesOtherPath(newVerification, "path", newPackage))
        {
            steps.Add(new InstallRootMigrationStep(
                InstallRootMigrationStepKind.RewriteVerificationRecord,
                newVerification,
                newPackage));
        }

        // Settings: every loose file in the old settings folder, then the
        // settings and key bindings an even older layout kept elsewhere.
        if (Directory.Exists(old.ConfigDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(old.ConfigDirectory)
                         .OrderBy(static file => file, StringComparer.Ordinal))
            {
                if (string.Equals(
                        Path.GetFileName(file),
                        MovedNoteFileName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Move(file, Path.Combine(paths.ConfigDirectory, Path.GetFileName(file)));
            }
        }

        if (old.EarlierConfigDirectory is { } earlier
            && !ApplicationPathIdentity.Equals(earlier, old.ConfigDirectory))
        {
            foreach (string name in EarlierSettingsFiles)
            {
                if (!File.Exists(Path.Combine(old.ConfigDirectory, name)))
                {
                    Move(
                        Path.Combine(earlier, name),
                        Path.Combine(paths.ConfigDirectory, name));
                }
            }
        }

        // Plugin code, then each plugin's private files into its own folder.
        string oldPlugins = Path.Combine(old.DataDirectory, "plugins");
        if (Directory.Exists(oldPlugins))
        {
            foreach (string plugin in Directory.EnumerateDirectories(oldPlugins)
                         .OrderBy(static folder => folder, StringComparer.Ordinal))
            {
                if (Path.GetFileName(plugin).StartsWith('.'))
                    continue;
                Move(plugin, Path.Combine(paths.PluginsDirectory, Path.GetFileName(plugin)));
            }
        }

        string oldStorage = Path.Combine(old.ConfigDirectory, "plugins");
        if (Directory.Exists(oldStorage))
        {
            foreach (string plugin in Directory.EnumerateDirectories(oldStorage)
                         .OrderBy(static folder => folder, StringComparer.Ordinal))
            {
                Move(plugin, paths.PluginFilesDirectory(Path.GetFileName(plugin)));
            }
        }

        Move(
            Path.Combine(old.DataDirectory, "vtank"),
            paths.VtankProfilesDirectory);
        Move(
            Path.Combine(old.DataDirectory, "journal"),
            paths.JournalDirectory);
        Move(
            Path.Combine(old.DataDirectory, "screenshots"),
            paths.ScreenshotsDirectory);
        Move(
            Path.Combine(old.DataDirectory, "logs"),
            paths.LogsDirectory);

        // Crash reports from both the launcher and the client.
        Move(
            Path.Combine(old.DataDirectory, "crash-reports"),
            paths.CrashReportsDirectory);
        string oldDiagnostics = Path.Combine(old.CacheDirectory, "diagnostics");
        if (Directory.Exists(oldDiagnostics))
        {
            foreach (string report in Directory.EnumerateFiles(oldDiagnostics, "crash-*.json")
                         .OrderBy(static file => file, StringComparer.Ordinal))
            {
                Move(report, Path.Combine(paths.CrashReportsDirectory, Path.GetFileName(report)));
            }
        }

        // A run interrupted between moving a record and rewriting it has no
        // moves left, only the rewrite; that still counts as work to finish.
        if (steps.Count == 0)
            return new InstallRootMigrationPlan([], []);

        IReadOnlyList<string> oldRoots = old.TopLevelRoots()
            .Where(Directory.Exists)
            .ToArray();
        return new InstallRootMigrationPlan(steps, oldRoots);
    }

    /// <summary>
    /// The old roots a finished migration came from, as its layout marker
    /// records them, keeping only those whose note still names this root.
    /// </summary>
    public static IReadOnlyList<string> ReadOldRoots(ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        try
        {
            if (!File.Exists(paths.LayoutMarkerFile)
                || JsonNode.Parse(File.ReadAllText(paths.LayoutMarkerFile))
                    is not JsonObject marker
                || marker["migratedFrom"] is not JsonArray roots)
            {
                return [];
            }

            return roots
                .Select(static node => node?.GetValue<string>())
                .Where(static root => !string.IsNullOrWhiteSpace(root))
                .Select(static root => root!)
                .Where(root => NoteNames(root, paths.RootDirectory))
                .ToArray();
        }
        catch (Exception ex) when (ex is JsonException
                                   or InvalidOperationException
                                   or IOException
                                   or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Whether a record exists and names a path other than <paramref name="expected"/>.</summary>
    private static bool NamesOtherPath(
        string recordPath,
        string propertyName,
        string expected)
    {
        if (!File.Exists(recordPath))
            return false;

        try
        {
            return JsonNode.Parse(File.ReadAllText(recordPath)) is not JsonObject record
                || record[propertyName] is not JsonValue value
                || !value.TryGetValue(out string? current)
                || !string.Equals(current, expected, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            // An unreadable record in the new root is the launcher's to
            // report and replace; there is nothing a rewrite could fix.
            return false;
        }
    }

    private static bool NoteNames(string oldRoot, string newRoot)
    {
        string note = Path.Combine(oldRoot, MovedNoteFileName);
        if (!File.Exists(note))
            return false;

        return File.ReadAllLines(note).Any(line =>
            !string.IsNullOrWhiteSpace(line)
            && Path.IsPathFullyQualified(line.Trim())
            && ApplicationPathIdentity.Equals(line.Trim(), newRoot));
    }

    private static IReadOnlyList<string> ReadKeptClientVersions(string oldApp)
    {
        string current = Path.Combine(oldApp, "current.json");
        try
        {
            if (!File.Exists(current)
                || JsonNode.Parse(File.ReadAllText(current)) is not JsonObject record)
            {
                return [];
            }

            var versions = new List<string>();
            foreach (string name in new[] { "currentVersion", "previousVersion" })
            {
                if (record[name] is JsonValue value
                    && value.TryGetValue(out string? version)
                    && IsPlainFolderName(version)
                    && !versions.Contains(version, StringComparer.OrdinalIgnoreCase))
                {
                    versions.Add(version);
                }
            }

            return versions;
        }
        catch (JsonException)
        {
            // Without a readable record there is no telling which version is
            // current; none are moved, and the launcher fetches the client again.
            return [];
        }
    }

    private static bool IsPlainFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name is not ("." or "..")
        && !name.Contains('/')
        && !name.Contains('\\');

    private static (IReadOnlyList<string> Moved, IReadOnlyList<string> Failures, IReadOnlyList<string> Conflicts) Execute(
        InstallRootMigrationPlan plan,
        Func<string, string, bool> sameVolume)
    {
        var moved = new List<string>();
        var failures = new List<string>();
        var conflicts = new List<string>();
        foreach (InstallRootMigrationStep step in plan.Steps)
        {
            try
            {
                switch (step.Kind)
                {
                    case InstallRootMigrationStepKind.Move:
                        if (InstallRootFileMover.MoveTree(
                                step.Source,
                                step.Destination,
                                sameVolume,
                                failures,
                                conflicts))
                        {
                            moved.Add(step.Destination);
                        }

                        break;
                    case InstallRootMigrationStepKind.RewriteInstallRecord:
                        RewritePathProperty(step.Source, "preparedAssetPath", step.Destination);
                        break;
                    case InstallRootMigrationStepKind.RewriteVerificationRecord:
                        RewritePathProperty(step.Source, "path", step.Destination);
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or JsonException
                                       or InvalidOperationException)
            {
                failures.Add($"{step.Source}: {ex.Message}");
            }
        }

        return (moved, failures, conflicts);
    }

    /// <summary>
    /// Points a root's install and verification records at that root's own
    /// prepared content, after the root itself was moved.
    /// </summary>
    public static void RewriteContentRecords(ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string package = Path.Combine(paths.GameDataDirectory, "pak", "acdream.pak");
        RewritePathProperty(
            Path.Combine(paths.GameDataDirectory, "install.json"),
            "preparedAssetPath",
            package);
        RewritePathProperty(
            Path.Combine(paths.GameDataDirectory, "install.verification.json"),
            "path",
            package);
    }

    /// <summary>
    /// Points a record at the new content path. The launcher refuses a record
    /// whose content path is not the canonical one for its root, so a moved
    /// record that still named the old path would read as a broken install.
    /// </summary>
    internal static void RewritePathProperty(
        string recordPath,
        string propertyName,
        string newPath)
    {
        if (!File.Exists(recordPath))
            return;

        JsonNode? parsed = JsonNode.Parse(File.ReadAllText(recordPath));
        if (parsed is not JsonObject record)
        {
            throw new InvalidOperationException(
                $"'{recordPath}' is not a JSON object.");
        }

        if (record[propertyName] is JsonValue current
            && current.TryGetValue(out string? existing)
            && string.Equals(existing, newPath, StringComparison.Ordinal))
        {
            return;
        }

        record[propertyName] = newPath;
        string temporary = recordPath + ".migrate.tmp";
        File.WriteAllText(
            temporary,
            record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, recordPath, overwrite: true);
    }

    private static string PendingConflictsPath(ApplicationPathSet paths) =>
        Path.Combine(paths.RootDirectory, ".migration.conflicts.json");

    private static IReadOnlyList<string> ReadPendingConflicts(ApplicationPathSet paths)
    {
        string path = PendingConflictsPath(paths);
        if (!File.Exists(path))
            return [];

        return JsonNode.Parse(File.ReadAllText(path)) is JsonArray array
            ? array
                .Select(static node => node?.GetValue<string>())
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!)
                .ToArray()
            : [];
    }

    private static void WritePendingConflicts(
        ApplicationPathSet paths,
        IReadOnlyList<string> conflicts)
    {
        string path = PendingConflictsPath(paths);
        if (conflicts.Count == 0)
        {
            File.Delete(path);
            return;
        }

        File.WriteAllText(
            path,
            new JsonArray(conflicts.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray())
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Marks a root as laid out, recording where a migration came from.</summary>
    public static void WriteLayoutMarker(
        ApplicationPathSet paths,
        IReadOnlyList<string> migratedFrom,
        IReadOnlyList<string>? conflicts = null,
        IReadOnlyList<string>? leftBehind = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(migratedFrom);
        Directory.CreateDirectory(paths.RootDirectory);
        var marker = new JsonObject { ["version"] = 1 };
        if (migratedFrom.Count > 0)
        {
            marker["migratedFrom"] = new JsonArray(
                migratedFrom.Select(static root => (JsonNode?)JsonValue.Create(root)).ToArray());
        }

        if (conflicts is { Count: > 0 })
        {
            marker["conflicts"] = new JsonArray(
                conflicts.Select(static path => (JsonNode?)JsonValue.Create(path)).ToArray());
        }

        // What each old folder still held when the migration finished: the
        // only entries "Remove the old folders" may delete.
        if (leftBehind is { Count: > 0 })
        {
            marker["leftBehind"] = new JsonArray(
                leftBehind.Select(static path => (JsonNode?)JsonValue.Create(path)).ToArray());
        }

        string temporary = paths.LayoutMarkerFile + ".tmp";
        File.WriteAllText(
            temporary,
            marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, paths.LayoutMarkerFile, overwrite: true);
    }

    /// <summary>Writes the note naming the new root; returns why it could not, or null.</summary>
    private static string? TryWriteMovedNote(string oldRoot, string newRoot)
    {
        try
        {
            if (!Directory.Exists(oldRoot))
                return null;

            File.WriteAllText(
                Path.Combine(oldRoot, MovedNoteFileName),
                "OpenAC now keeps everything in one folder:"
                + Environment.NewLine
                + newRoot
                + Environment.NewLine
                + Environment.NewLine
                + "What is left here was not needed there (older client versions, caches). "
                + "The launcher's settings can remove this folder."
                + Environment.NewLine);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The move itself succeeded; without the note the old folder is
            // simply not offered for removal.
            return $"{oldRoot}: the note naming the new folder could not be written: {ex.Message}";
        }
    }

    /// <summary>
    /// Whether a rename can move between two paths. On Windows that is the
    /// same drive; elsewhere the rename is tried and a cross-device refusal
    /// falls back to copying.
    /// </summary>
    internal static bool SameVolume(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        return string.Equals(
            Path.GetPathRoot(Path.GetFullPath(source)),
            Path.GetPathRoot(Path.GetFullPath(destination)),
            StringComparison.OrdinalIgnoreCase);
    }
}
