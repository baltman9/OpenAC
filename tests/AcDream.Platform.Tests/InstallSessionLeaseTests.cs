using AcDream.Platform;

namespace AcDream.Platform.Tests;

public sealed class InstallSessionLeaseTests : IDisposable
{
    private readonly ApplicationPathSet _paths = ApplicationPathSet.ForRoot(
        Path.Combine(Path.GetTempPath(), "openac-lease-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(_paths.RootDirectory))
            Directory.Delete(_paths.RootDirectory, recursive: true);
    }

    /// <summary>
    /// Any number of clients share the lock; a writer needs it alone and
    /// keeps new clients out while it holds it. Mutation: opening the shared
    /// lease without sharing, or the exclusive one with sharing, fails this.
    /// </summary>
    [Fact]
    public void SessionsShareTheLockAndAWriterNeedsItAlone()
    {
        using (InstallSessionLease? first = InstallSessionLease.TryAcquireShared(_paths))
        using (InstallSessionLease? second = InstallSessionLease.TryAcquireShared(_paths))
        {
            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Null(InstallSessionLease.TryAcquireExclusive(_paths));
        }

        using (InstallSessionLease? writer = InstallSessionLease.TryAcquireExclusive(_paths))
        {
            Assert.NotNull(writer);
            Assert.Null(InstallSessionLease.TryAcquireShared(_paths));
        }

        Assert.Equal(
            Path.Combine(_paths.RootDirectory, "app", ".update-session.lock"),
            InstallSessionLease.LockPath(_paths));
    }

    /// <summary>
    /// A client started by hand holds the lock, so the old folders are not
    /// removed under it. Mutation: removing without the exclusive lock fails this.
    /// </summary>
    [Fact]
    public void OldFoldersAreNotRemovedWhileAClientRuns()
    {
        string old = Path.Combine(_paths.RootDirectory + "-old", "acdream");
        Directory.CreateDirectory(old);
        File.WriteAllText(
            Path.Combine(old, InstallRootMigration.MovedNoteFileName),
            "moved" + Environment.NewLine + _paths.RootDirectory + Environment.NewLine);
        InstallRootMigration.WriteLayoutMarker(_paths, [old]);
        try
        {
            using InstallSessionLease? client = InstallSessionLease.TryAcquireShared(_paths);

            IReadOnlyList<string> refusals = OldRootRemoval.Remove(_paths);

            Assert.Equal([OldRootRemoval.SessionRefusal], refusals);
            Assert.True(Directory.Exists(old));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(old)!, recursive: true);
        }
    }
}
