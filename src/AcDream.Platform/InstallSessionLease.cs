namespace AcDream.Platform;

/// <summary>
/// The install's session lock, <c>app/.update-session.lock</c>. Every running
/// client, windowless host and launcher session holds it shared for its whole
/// lifetime; anything that rewrites the install underneath them (a client
/// update, moving the install folder, removing the old folders) needs it
/// exclusively, and so refuses while any of them runs, however it was
/// started. The launcher's own barrier opens the same file the same way.
/// </summary>
public sealed class InstallSessionLease : IDisposable
{
    /// <summary>The lock file's name inside <c>app/</c>.</summary>
    public const string LockFileName = ".update-session.lock";

    private FileStream? _stream;

    private InstallSessionLease(FileStream stream) => _stream = stream;

    /// <summary>The lock file of an install.</summary>
    public static string LockPath(ApplicationPathSet paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.AppDirectory, LockFileName);
    }

    /// <summary>
    /// Takes the lock alongside every other running session, or returns null
    /// while something holds it exclusively (an update or a move).
    /// </summary>
    public static InstallSessionLease? TryAcquireShared(ApplicationPathSet paths) =>
        TryOpen(paths, FileShare.ReadWrite);

    /// <summary>Takes the lock alone, or returns null while any session or update holds it.</summary>
    public static InstallSessionLease? TryAcquireExclusive(ApplicationPathSet paths) =>
        TryOpen(paths, FileShare.None);

    /// <summary>What a starting client or bot says when it cannot take the lock.</summary>
    public static string BusyMessage(ApplicationPathSet paths) =>
        $"A client update or a move of the install folder is running in {paths.RootDirectory}. "
        + "Start again when it has finished.";

    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();

    private static InstallSessionLease? TryOpen(ApplicationPathSet paths, FileShare share)
    {
        string path = LockPath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new InstallSessionLease(new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                share,
                bufferSize: 1,
                FileOptions.None));
        }
        catch (IOException)
        {
            // Held in a way this request cannot share: that is the answer.
            return null;
        }
    }
}
