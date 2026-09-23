using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcDream.Platform;

/// <summary>One entry "Remove the old folders" would delete.</summary>
/// <param name="Path">The file or folder.</param>
/// <param name="Bytes">Its size, folders counted with everything inside.</param>
public sealed record OldRootRemovalItem(string Path, long Bytes)
{
    /// <summary>The size as a player reads it, the same in every locale.</summary>
    public string Size => FormatBytes(Bytes);

    /// <summary>A byte count in B, KB, MB or GB with one decimal.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }
}

/// <summary>
/// What "Remove the old folders" would delete, or why it will not. Only the
/// entries the migration deliberately left in each old folder are deleted;
/// anything that appeared or changed there after the migration means
/// something still uses the old folder, and nothing is deleted at all.
/// </summary>
public sealed record OldRootRemovalPlan(
    IReadOnlyList<OldRootRemovalItem> Items,
    IReadOnlyList<string> Refusals)
{
    /// <summary>True when the removal may go ahead.</summary>
    public bool CanRemove => Refusals.Count == 0 && Items.Count > 0;

    /// <summary>The bytes the removal frees.</summary>
    public long TotalBytes => Items.Sum(static item => item.Bytes);
}

/// <summary>Plans and performs "Remove the old folders".</summary>
public static class OldRootRemoval
{
    /// <summary>Why the old folders are not removed while anything runs.</summary>
    public const string SessionRefusal =
        "Close every OpenAC client and bot before removing the old folders.";

    /// <summary>Works out what removing the old folders would delete.</summary>
    public static OldRootRemovalPlan Plan(ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var items = new List<OldRootRemovalItem>();
        var refusals = new List<string>();
        IReadOnlyList<string> roots = InstallRootMigration.ReadOldRoots(paths);
        if (roots.Count == 0)
            return new OldRootRemovalPlan(items, refusals);

        DateTime migratedAt = File.GetLastWriteTimeUtc(paths.LayoutMarkerFile);
        var leftBehind = new HashSet<string>(
            ReadLeftBehind(paths).Select(Full),
            PathComparer);
        foreach (string root in roots)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(root)
                         .OrderBy(static entry => entry, StringComparer.Ordinal))
            {
                bool isNote = string.Equals(
                    Path.GetFileName(entry),
                    InstallRootMigration.MovedNoteFileName,
                    StringComparison.OrdinalIgnoreCase);
                if (!isNote && !leftBehind.Contains(Full(entry)))
                {
                    refusals.Add(
                        $"{entry} appeared in the old folder after the move, so something "
                        + "still uses it.");
                    continue;
                }

                long bytes = 0;
                foreach (FileInfo file in Files(entry))
                {
                    bytes += file.Length;
                    if (!isNote && file.LastWriteTimeUtc > migratedAt)
                    {
                        refusals.Add(
                            $"{file.FullName} changed after the move, so something still "
                            + "uses the old folder.");
                    }
                }

                items.Add(new OldRootRemovalItem(entry, bytes));
            }
        }

        return new OldRootRemovalPlan(items, refusals);
    }

    /// <summary>
    /// Deletes what <see cref="Plan"/> lists, checking again first. Returns
    /// what could not be deleted, or why nothing was.
    /// </summary>
    public static IReadOnlyList<string> Remove(ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        // A running client or bot (started by the launcher or by hand) holds
        // the session lock; nothing is deleted while one could still be
        // reading from an old folder.
        using InstallSessionLease? exclusive = InstallSessionLease.TryAcquireExclusive(paths);
        if (exclusive is null)
            return [SessionRefusal];

        OldRootRemovalPlan plan = Plan(paths);
        if (plan.Refusals.Count > 0)
            return plan.Refusals;

        var failures = new List<string>();
        foreach (OldRootRemovalItem item in plan.Items)
        {
            try
            {
                if (Directory.Exists(item.Path))
                    Directory.Delete(item.Path, recursive: true);
                else if (File.Exists(item.Path))
                    File.Delete(item.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{item.Path}: {ex.Message}");
            }
        }

        foreach (string root in plan.Items
                     .Select(static item => Path.GetDirectoryName(item.Path)!)
                     .Distinct(PathComparer))
        {
            try
            {
                if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                    Directory.Delete(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{root}: {ex.Message}");
            }
        }

        return failures;
    }

    /// <summary>The entries the finished migration recorded as deliberately left behind.</summary>
    internal static IReadOnlyList<string> ReadLeftBehind(ApplicationPathSet paths)
    {
        try
        {
            if (!File.Exists(paths.LayoutMarkerFile)
                || JsonNode.Parse(File.ReadAllText(paths.LayoutMarkerFile)) is not JsonObject marker
                || marker["leftBehind"] is not JsonArray entries)
            {
                return [];
            }

            return entries
                .Select(static node => node?.GetValue<string>())
                .Where(static entry => !string.IsNullOrWhiteSpace(entry))
                .Select(static entry => entry!)
                .ToArray();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>The top-level entries of the old roots, taken when the migration finishes.</summary>
    internal static IReadOnlyList<string> SnapshotLeftBehind(IReadOnlyList<string> oldRoots) =>
        oldRoots
            .Where(Directory.Exists)
            .SelectMany(static root => Directory.EnumerateFileSystemEntries(root))
            .Where(static entry => !string.Equals(
                Path.GetFileName(entry),
                InstallRootMigration.MovedNoteFileName,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static entry => entry, StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<FileInfo> Files(string entry)
    {
        if (File.Exists(entry))
            return [new FileInfo(entry)];

        return new DirectoryInfo(entry).EnumerateFiles("*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
        });
    }

    private static string Full(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
