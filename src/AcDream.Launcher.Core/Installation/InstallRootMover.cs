using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Installation;

/// <summary>How a move of the install folder ended.</summary>
/// <param name="Moved">True when the install now lives at the new folder.</param>
/// <param name="Message">What to tell the player.</param>
/// <param name="Leftovers">Old entries that could not be deleted after a successful move.</param>
public sealed record InstallRootMoveResult(
    bool Moved,
    string Message,
    IReadOnlyList<string> Leftovers)
{
    internal static InstallRootMoveResult Refused(string message) =>
        new(false, message, []);
}

/// <summary>
/// Moves the whole install folder somewhere the player picked, then points
/// the default folder at it. Nothing may be running from the install while
/// it moves, so the move holds the update/session lock the launcher's
/// sessions and updates already share. On one volume each entry is renamed
/// (and renamed back if one refuses); across volumes everything is copied
/// first and the old folder is deleted only after the copy is complete and
/// the pointer written, so a failure leaves the old install untouched.
/// The rebuildable cache is not carried.
/// </summary>
public sealed class InstallRootMover
{
    /// <summary>Why a move was refused while something runs from the install.</summary>
    public const string SessionRefusal =
        "Close every OpenAC client and bot before moving the install folder.";

    private const string CacheFolderName = "cache";

    private readonly ApplicationPathSet _paths;
    private readonly string _defaultRoot;
    private readonly UpdateSessionBarrier _barrier;
    private readonly Func<string, string, bool> _sameVolume;

    /// <param name="paths">The install as this launcher resolved it.</param>
    /// <param name="defaultRoot">The default folder the pointer lives in; the per-user default when omitted.</param>
    /// <param name="sameVolume">Whether a rename can move between two paths; tests force the copy path.</param>
    public InstallRootMover(
        ApplicationPathSet paths,
        string? defaultRoot = null,
        Func<string, string, bool>? sameVolume = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _defaultRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            defaultRoot ?? ApplicationPathSet.ResolveDefaultRoot()));
        _barrier = new UpdateSessionBarrier(paths.DataDirectory);
        _sameVolume = sameVolume ?? DefaultSameVolume;
    }

    /// <summary>The folder being moved.</summary>
    public string RootDirectory => _paths.RootDirectory;

    /// <summary>
    /// Why this install cannot be moved from here at all, or null. A folder
    /// named on the command line or in the environment is not the pointer's
    /// to change: the next start would use that name again.
    /// </summary>
    public string? UnavailableReason =>
        _paths.RootSource is ApplicationRootSource.Pointer or ApplicationRootSource.Default
            ? null
            : "This install folder was set on the command line or in the environment, "
              + "so it has to be changed there.";

    /// <summary>Why <paramref name="target"/> cannot receive the install, or null.</summary>
    public string? ValidateTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target) || !Path.IsPathFullyQualified(target))
            return "Choose a full folder path.";

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (ApplicationPathIdentity.Equals(full, RootDirectory))
            return "The install is already in that folder.";
        if (ApplicationPathIdentity.IsSameOrInside(full, RootDirectory))
            return "The new folder cannot be inside the current install folder.";
        if (ApplicationPathIdentity.IsSameOrInside(RootDirectory, full))
            return "The new folder cannot contain the current install folder.";
        if (Directory.Exists(full)
            && Directory.EnumerateFileSystemEntries(full).Any(entry => !IsPointerOfDefault(entry)))
        {
            return "Choose an empty folder, or one that does not exist yet.";
        }

        return File.Exists(full) ? "A file already has that name." : null;
    }

    /// <summary>Moves the install to <paramref name="target"/>.</summary>
    /// <param name="progress">Told the name of each entry as it moves.</param>
    public InstallRootMoveResult Move(string target, Action<string>? progress = null)
    {
        if (UnavailableReason is { } unavailable)
            return InstallRootMoveResult.Refused(unavailable);
        if (ValidateTarget(target) is { } invalid)
            return InstallRootMoveResult.Refused(invalid);

        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (!_barrier.TryAcquireExclusive(out UpdateSessionBarrier.ExclusiveLease? lease))
            return InstallRootMoveResult.Refused(SessionRefusal);

        bool destinationExisted = Directory.Exists(destination);
        bool sameVolume = _sameVolume(RootDirectory, destination);
        using (lease)
        {
            Directory.CreateDirectory(destination);
            string? failure = sameVolume
                ? RenameInto(destination, progress)
                : CopyInto(destination, destinationExisted, progress);
            if (failure is not null)
            {
                return InstallRootMoveResult.Refused(
                    "The install folder was not moved: " + failure);
            }

            ApplicationPathSet moved = ApplicationPathSet.ForRoot(destination);
            InstallRootMigration.RewriteContentRecords(moved);
            ApplicationRootPointer.Write(_defaultRoot, destination);
        }

        // The lock is released before the old folder goes: it lives there.
        IReadOnlyList<string> leftovers = DeleteOldRoot();
        return new InstallRootMoveResult(
            true,
            leftovers.Count == 0
                ? $"The install folder is now {destination}."
                : $"The install folder is now {destination}. Some old files could not be "
                  + $"deleted from {RootDirectory}; they are no longer used.",
            leftovers);
    }

    /// <summary>The entries that move: everything but the cache and the lock file.</summary>
    private IEnumerable<string> EntriesToMove(string folder, bool isRoot)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(folder)
                     .OrderBy(static entry => entry, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(entry);
            if (isRoot && string.Equals(name, CacheFolderName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (isRoot && IsPointerOfDefault(entry))
                continue;
            if (ApplicationPathIdentity.Equals(entry, _barrier.LockPath))
                continue;
            yield return entry;
        }
    }

    private string? RenameInto(string destination, Action<string>? progress)
    {
        var renamed = new List<(string From, string To)>();
        try
        {
            foreach (string entry in EntriesToMove(RootDirectory, isRoot: true))
            {
                string to = Path.Combine(destination, Path.GetFileName(entry));
                if (ApplicationPathIdentity.Equals(entry, _paths.AppDirectory))
                {
                    // The app folder holds the lock this move is holding, so
                    // its entries move one by one around it.
                    Directory.CreateDirectory(to);
                    foreach (string child in EntriesToMove(entry, isRoot: false))
                    {
                        string childTo = Path.Combine(to, Path.GetFileName(child));
                        progress?.Invoke(Path.GetFileName(entry) + "/" + Path.GetFileName(child));
                        Rename(child, childTo);
                        renamed.Add((child, childTo));
                    }

                    continue;
                }

                progress?.Invoke(Path.GetFileName(entry));
                Rename(entry, to);
                renamed.Add((entry, to));
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            for (int index = renamed.Count - 1; index >= 0; index--)
            {
                Rename(renamed[index].To, renamed[index].From);
            }

            return ex.Message;
        }
    }

    private string? CopyInto(string destination, bool destinationExisted, Action<string>? progress)
    {
        try
        {
            foreach (string entry in EntriesToMove(RootDirectory, isRoot: true))
            {
                progress?.Invoke(Path.GetFileName(entry));
                CopyEntry(entry, Path.Combine(destination, Path.GetFileName(entry)));
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only what this move created is removed; the old install is
            // untouched and still the one in use.
            foreach (string entry in EntriesToMove(RootDirectory, isRoot: true))
            {
                string copied = Path.Combine(destination, Path.GetFileName(entry));
                if (Directory.Exists(copied))
                    Directory.Delete(copied, recursive: true);
                else if (File.Exists(copied))
                    File.Delete(copied);
            }

            if (!destinationExisted && !Directory.EnumerateFileSystemEntries(destination).Any())
                Directory.Delete(destination);

            return ex.Message;
        }
    }

    private void CopyEntry(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: false);
            File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (string child in EntriesToMove(source, isRoot: false))
        {
            CopyEntry(child, Path.Combine(destination, Path.GetFileName(child)));
        }
    }

    /// <summary>
    /// Deletes what is left of the old install. The default folder keeps its
    /// pointer, which is what now leads every start to the new folder.
    /// </summary>
    private IReadOnlyList<string> DeleteOldRoot()
    {
        var leftovers = new List<string>();
        if (!Directory.Exists(RootDirectory))
            return leftovers;

        foreach (string entry in Directory.EnumerateFileSystemEntries(RootDirectory))
        {
            if (IsPointerOfDefault(entry))
                continue;
            try
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                leftovers.Add($"{entry}: {ex.Message}");
            }
        }

        if (leftovers.Count == 0 && !ApplicationPathIdentity.Equals(RootDirectory, _defaultRoot))
        {
            try
            {
                Directory.Delete(RootDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                leftovers.Add($"{RootDirectory}: {ex.Message}");
            }
        }

        return leftovers;
    }

    private bool IsPointerOfDefault(string entry) =>
        ApplicationPathIdentity.Equals(entry, ApplicationRootPointer.PathFor(_defaultRoot));

    private static void Rename(string from, string to)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    private static bool DefaultSameVolume(string left, string right)
    {
        if (!OperatingSystem.IsWindows())
        {
            // There is no portable way to ask whether two paths share a
            // device, so elsewhere the install is copied: the copy path is
            // the one that can never leave it half moved.
            return false;
        }

        return string.Equals(
            Path.GetPathRoot(Path.GetFullPath(left)),
            Path.GetPathRoot(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }
}
