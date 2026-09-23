namespace AcDream.Platform;

/// <summary>
/// Moves a file or folder tree into place, merging into a folder that already
/// exists. On one volume a rename moves the whole tree at once; across volumes
/// each file is copied beside its destination, renamed into place, and only
/// then deleted at the source, so an interrupted move never leaves a
/// half-written file under its real name and can simply be run again.
/// </summary>
public static class InstallRootFileMover
{
    private const string PartialSuffix = ".moving";

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/>. A
    /// destination file that already exists is kept and its source left where
    /// it is. Returns true when nothing is left at the source.
    /// </summary>
    /// <param name="sameVolume">Whether a rename can move between two paths.</param>
    /// <param name="failures">Receives one line per entry that could not be moved.</param>
    /// <param name="onFile">Called with each file's destination as it lands.</param>
    public static bool MoveTree(
        string source,
        string destination,
        Func<string, string, bool> sameVolume,
        ICollection<string> failures,
        Action<string>? onFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(sameVolume);
        ArgumentNullException.ThrowIfNull(failures);

        if (File.Exists(source))
            return MoveFile(source, destination, sameVolume, failures, onFile);

        if (!Directory.Exists(source))
            return true;

        if (!Directory.Exists(destination)
            && !File.Exists(destination)
            && sameVolume(source, destination))
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(destination))!);
            try
            {
                Directory.Move(source, destination);
                onFile?.Invoke(destination);
                return true;
            }
            catch (IOException ex) when (OperatingSystem.IsWindows())
            {
                // On one Windows drive a refused rename means something in
                // the tree is open. Copying it anyway would leave two live
                // copies; it is reported and the next start tries again.
                failures.Add($"{source}: {ex.Message}");
                return false;
            }
            catch (IOException)
            {
                // Elsewhere a refused rename is most often a different
                // device behind one path prefix; the merge below copies.
            }
        }

        Directory.CreateDirectory(destination);
        bool complete = true;
        foreach (string entry in Directory.EnumerateFileSystemEntries(source)
                     .OrderBy(static entry => entry, StringComparer.Ordinal))
        {
            complete &= MoveTree(
                entry,
                Path.Combine(destination, Path.GetFileName(entry)),
                sameVolume,
                failures,
                onFile);
        }

        if (complete)
        {
            try
            {
                Directory.Delete(source);
            }
            catch (IOException ex)
            {
                failures.Add($"{source}: {ex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                failures.Add($"{source}: {ex.Message}");
                return false;
            }
        }

        return complete;
    }

    private static bool MoveFile(
        string source,
        string destination,
        Func<string, string, bool> sameVolume,
        ICollection<string> failures,
        Action<string>? onFile)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            // The new root already has its own copy; that one wins.
            return false;
        }

        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(destination))!);
            if (sameVolume(source, destination))
            {
                try
                {
                    File.Move(source, destination);
                    onFile?.Invoke(destination);
                    return true;
                }
                catch (IOException) when (!OperatingSystem.IsWindows()
                                           && File.Exists(source)
                                           && !File.Exists(destination))
                {
                    // A different device behind one path prefix: copy below.
                }
            }

            string partial = destination + PartialSuffix;
            File.Copy(source, partial, overwrite: true);
            File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(source));
            File.Move(partial, destination);
            File.Delete(source);
            onFile?.Invoke(destination);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{source}: {ex.Message}");
            return false;
        }
    }
}
