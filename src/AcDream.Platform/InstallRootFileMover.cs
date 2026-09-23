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
    /// <summary>The suffix a moved file takes when its destination already has a file of that name.</summary>
    public const string ConflictSuffix = ".from-old";

    private const string PartialSuffix = ".moving";

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/>.
    /// Returns true when nothing is left at the source. A destination file
    /// that already exists is a conflict: it stays as it is, and the moved
    /// file lands beside it as <c>&lt;name&gt;.from-old</c> (then
    /// <c>.from-old-2</c>, …), so neither copy is ever lost.
    /// </summary>
    /// <param name="sameVolume">Whether a rename can move between two paths.</param>
    /// <param name="failures">Receives one line per entry that could not be moved.</param>
    /// <param name="conflicts">Receives the path each conflicting file was moved to.</param>
    /// <param name="onFile">Called with each file's destination as it lands.</param>
    public static bool MoveTree(
        string source,
        string destination,
        Func<string, string, bool> sameVolume,
        ICollection<string> failures,
        ICollection<string> conflicts,
        Action<string>? onFile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(sameVolume);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(conflicts);

        if (File.Exists(source))
            return MoveFile(source, destination, sameVolume, failures, conflicts, onFile);

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
                conflicts,
                onFile);
        }

        if (complete)
        {
            try
            {
                Directory.Delete(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{source}: {ex.Message}");
                return false;
            }
        }

        return complete;
    }

    /// <summary>The first free <c>.from-old</c> name beside <paramref name="destination"/>.</summary>
    public static string ConflictPath(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        string candidate = destination + ConflictSuffix;
        for (int attempt = 2; File.Exists(candidate) || Directory.Exists(candidate); attempt++)
        {
            candidate = $"{destination}{ConflictSuffix}-{attempt}";
        }

        return candidate;
    }

    private static bool MoveFile(
        string source,
        string destination,
        Func<string, string, bool> sameVolume,
        ICollection<string> failures,
        ICollection<string> conflicts,
        Action<string>? onFile)
    {
        bool conflict = File.Exists(destination) || Directory.Exists(destination);
        string target = conflict ? ConflictPath(destination) : destination;
        try
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(Path.GetFullPath(target))!);
            if (sameVolume(source, target))
            {
                try
                {
                    File.Move(source, target);
                    Landed(target);
                    return true;
                }
                catch (IOException) when (!OperatingSystem.IsWindows()
                                           && File.Exists(source)
                                           && !File.Exists(target))
                {
                    // A different device behind one path prefix: copy below.
                }
            }

            string partial = target + PartialSuffix;
            File.Copy(source, partial, overwrite: true);
            File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(source));
            File.Move(partial, target);
            File.Delete(source);
            Landed(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failures.Add($"{source}: {ex.Message}");
            return false;
        }

        void Landed(string path)
        {
            if (conflict)
                conflicts.Add(path);
            onFile?.Invoke(path);
        }
    }
}
