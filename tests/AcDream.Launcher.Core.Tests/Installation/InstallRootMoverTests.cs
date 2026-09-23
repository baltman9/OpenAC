using System.Text.Json.Nodes;
using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Installation;

public sealed class InstallRootMoverTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "openac-move-" + Guid.NewGuid().ToString("N"));

    private readonly string _defaultRoot;
    private readonly ApplicationPathSet _paths;

    public InstallRootMoverTests()
    {
        _defaultRoot = Path.Combine(_scratch, "Local", "OpenAC");
        _paths = ApplicationPathSet.ForRoot(_defaultRoot, ApplicationRootSource.Default);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
            Directory.Delete(_scratch, recursive: true);
    }

    /// <summary>
    /// Mutation: skipping the record rewrite, the pointer write, or carrying
    /// the cache fails this.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MoveCarriesTheInstallAndPointsTheDefaultAtIt(bool sameVolume)
    {
        SeedInstall();
        string target = Path.Combine(_scratch, "Games", "OpenAC");
        var mover = new InstallRootMover(_paths, _defaultRoot, (_, _) => sameVolume);

        InstallRootMoveResult result = mover.Move(target);

        Assert.True(result.Moved, result.Message);
        Assert.Empty(result.Leftovers);
        Assert.True(File.Exists(Path.Combine(target, "app", "current.json")));
        Assert.True(File.Exists(Path.Combine(target, "data", "pak", "acdream.pak")));
        Assert.True(File.Exists(Path.Combine(target, "settings", "settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "plugins", "p", "files", "a.json")));
        Assert.False(Directory.Exists(Path.Combine(target, "cache")));
        Assert.Equal(
            Path.Combine(target, "data", "pak", "acdream.pak"),
            ((JsonObject)JsonNode.Parse(File.ReadAllText(
                Path.Combine(target, "data", "install.json")))!)["preparedAssetPath"]!
                .GetValue<string>());
        Assert.Equal(
            target,
            ApplicationRootPointer.Parse(File.ReadAllText(
                ApplicationRootPointer.PathFor(_defaultRoot))));
        Assert.Equal(
            [ApplicationRootPointer.PathFor(_defaultRoot)],
            Directory.EnumerateFileSystemEntries(_defaultRoot).ToArray());
    }

    /// <summary>Mutation: not taking the update/session lock fails this.</summary>
    [Fact]
    public void MoveIsRefusedWhileASessionRuns()
    {
        SeedInstall();
        using UpdateSessionBarrier.SessionLease session =
            new UpdateSessionBarrier(_paths.DataDirectory).AcquireSession();
        var mover = new InstallRootMover(_paths, _defaultRoot, (_, _) => true);

        InstallRootMoveResult result = mover.Move(Path.Combine(_scratch, "Games", "OpenAC"));

        Assert.False(result.Moved);
        Assert.Equal(InstallRootMover.SessionRefusal, result.Message);
        Assert.True(File.Exists(Path.Combine(_defaultRoot, "settings", "settings.json")));
        Assert.False(File.Exists(ApplicationRootPointer.PathFor(_defaultRoot)));
    }

    /// <summary>
    /// A client or bot started by hand holds the same session lock the
    /// launcher's barrier uses, so the move refuses under it too. Mutation:
    /// giving the barrier a different lock file fails this.
    /// </summary>
    [Fact]
    public void MoveIsRefusedWhileAHandStartedClientRuns()
    {
        SeedInstall();
        using InstallSessionLease? client = InstallSessionLease.TryAcquireShared(_paths);
        var mover = new InstallRootMover(_paths, _defaultRoot, (_, _) => true);

        InstallRootMoveResult result = mover.Move(Path.Combine(_scratch, "Games", "OpenAC"));

        Assert.NotNull(client);
        Assert.False(result.Moved);
        Assert.Equal(InstallRootMover.SessionRefusal, result.Message);
        Assert.Equal(
            InstallSessionLease.LockPath(_paths),
            new UpdateSessionBarrier(_paths.DataDirectory).LockPath);
    }

    /// <summary>Mutation: letting a command-line root be moved through the pointer fails this.</summary>
    [Fact]
    public void AnExplicitlyNamedRootCannotBeMovedFromTheLauncher()
    {
        var mover = new InstallRootMover(
            ApplicationPathSet.ForRoot(Path.Combine(_scratch, "explicit")),
            _defaultRoot);

        Assert.NotNull(mover.UnavailableReason);
        Assert.False(mover.Move(Path.Combine(_scratch, "Games")).Moved);
    }

    /// <summary>Mutation: accepting a non-empty or nested target fails this.</summary>
    [Fact]
    public void TargetsThatWouldMixOrNestAreRefused()
    {
        SeedInstall();
        string occupied = Path.Combine(_scratch, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "x.txt"), "x");
        var mover = new InstallRootMover(_paths, _defaultRoot);

        Assert.NotNull(mover.ValidateTarget(occupied));
        Assert.NotNull(mover.ValidateTarget(Path.Combine(_defaultRoot, "inner")));
        Assert.NotNull(mover.ValidateTarget(_scratch));
        Assert.NotNull(mover.ValidateTarget("relative"));
        Assert.Null(mover.ValidateTarget(Path.Combine(_scratch, "fresh")));
    }

    /// <summary>
    /// Moving back to the default folder removes the pointer.
    /// Mutation: writing a pointer that names the default itself fails this.
    /// </summary>
    [Fact]
    public void MovingBackToTheDefaultRemovesThePointer()
    {
        string elsewhere = Path.Combine(_scratch, "Games", "OpenAC");
        ApplicationRootPointer.Write(_defaultRoot, elsewhere);
        ApplicationPathSet pointed = ApplicationPathSet.ForRoot(elsewhere, ApplicationRootSource.Pointer);
        Directory.CreateDirectory(pointed.ConfigDirectory);
        File.WriteAllText(pointed.SettingsFile, "{}");
        var mover = new InstallRootMover(pointed, _defaultRoot, (_, _) => true);

        InstallRootMoveResult result = mover.Move(_defaultRoot);

        Assert.True(result.Moved, result.Message);
        Assert.True(File.Exists(Path.Combine(_defaultRoot, "settings", "settings.json")));
        Assert.False(File.Exists(ApplicationRootPointer.PathFor(_defaultRoot)));
        Assert.False(Directory.Exists(elsewhere));
    }

    /// <summary>
    /// A copy that fails part-way leaves the old install whole and in use.
    /// Mutation: writing the pointer before the copy completes, or not
    /// removing the partial copy, fails this.
    /// </summary>
    [Fact]
    [Trait("Lane", "Windows")]
    public void AFailedCopyLeavesTheOldInstallUntouched()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Lane=Windows requires a Windows host.");

        SeedInstall();
        string target = Path.Combine(_scratch, "Games", "OpenAC");
        var mover = new InstallRootMover(_paths, _defaultRoot, (_, _) => false);
        InstallRootMoveResult result;
        using (new FileStream(
                   Path.Combine(_defaultRoot, "settings", "settings.json"),
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            result = mover.Move(target);
        }

        Assert.False(result.Moved);
        Assert.False(File.Exists(ApplicationRootPointer.PathFor(_defaultRoot)));
        Assert.True(File.Exists(Path.Combine(_defaultRoot, "app", "current.json")));
        Assert.False(Directory.Exists(target));
    }

    /// <summary>
    /// A rename refused part-way is undone. Mutation: dropping the rollback
    /// loop fails this.
    /// </summary>
    [Fact]
    [Trait("Lane", "Windows")]
    public void ARefusedRenameIsRolledBack()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Lane=Windows requires a Windows host.");

        SeedInstall();
        string target = Path.Combine(_scratch, "Games", "OpenAC");
        var mover = new InstallRootMover(_paths, _defaultRoot, (_, _) => true);
        InstallRootMoveResult result;
        using (new FileStream(
                   Path.Combine(_defaultRoot, "settings", "settings.json"),
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            result = mover.Move(target);
        }

        Assert.False(result.Moved);
        Assert.True(File.Exists(Path.Combine(_defaultRoot, "app", "current.json")));
        Assert.True(File.Exists(Path.Combine(_defaultRoot, "data", "pak", "acdream.pak")));
        Assert.False(File.Exists(ApplicationRootPointer.PathFor(_defaultRoot)));

        // Nothing half-made is left at the target, so the same folder can be
        // tried again once the file is closed. Mutation: not removing the
        // folders the move created fails this.
        Assert.False(Directory.Exists(target));
        InstallRootMoveResult retry = mover.Move(target);
        Assert.True(retry.Moved, retry.Message);
    }

    /// <summary>
    /// The pointer is written as soon as the install is whole at the new
    /// folder; a record that cannot be rewritten then does not undo the move
    /// (the next launcher start repairs it). Mutation: rewriting the record
    /// before the pointer, so a bad record fails the whole move, fails this.
    /// </summary>
    [Fact]
    public void AnUnreadableInstallRecordDoesNotStopTheMove()
    {
        SeedInstall();
        Write("data/install.json", "not json");
        string target = Path.Combine(_scratch, "Games", "OpenAC");
        var mover = new InstallRootMover(_paths, _defaultRoot, (_, _) => true);

        InstallRootMoveResult result = mover.Move(target);

        Assert.True(result.Moved, result.Message);
        Assert.Equal(
            target,
            ApplicationRootPointer.Parse(File.ReadAllText(ApplicationRootPointer.PathFor(_defaultRoot))));
        Assert.Contains(result.Leftovers, line => line.Contains("next start", StringComparison.Ordinal));
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
        string elsewhere = Path.Combine(_scratch, "Elsewhere", "OpenAC", "data", "pak", "acdream.pak");
        Write(
            "data/install.json",
            "{ \"preparedAssetPath\": " + System.Text.Json.JsonSerializer.Serialize(elsewhere) + " }");

        Assert.Empty(InstallRootMover.RepairContentRecords(_paths));

        Write("data/pak/acdream.pak", "pak");
        Assert.Equal([record], InstallRootMover.RepairContentRecords(_paths));
        Assert.Equal(
            package,
            ((JsonObject)JsonNode.Parse(File.ReadAllText(record))!)["preparedAssetPath"]!
                .GetValue<string>());
        Assert.Empty(InstallRootMover.RepairContentRecords(_paths));

        string inside = Path.Combine(_paths.GameDataDirectory, "custom.pak");
        Write(
            "data/install.json",
            "{ \"preparedAssetPath\": " + System.Text.Json.JsonSerializer.Serialize(inside) + " }");
        Assert.Empty(InstallRootMover.RepairContentRecords(_paths));
    }

    private void SeedInstall()
    {
        Write("app/current.json", "{}");
        Write("app/0.1.15/AcDream.App.exe", "x");
        Write("data/pak/acdream.pak", "pak");
        Write(
            "data/install.json",
            "{ \"preparedAssetPath\": "
            + System.Text.Json.JsonSerializer.Serialize(
                Path.Combine(_defaultRoot, "data", "pak", "acdream.pak"))
            + " }");
        Write("settings/settings.json", "{}");
        Write("plugins/p/plugin.json", "{}");
        Write("plugins/p/files/a.json", "{}");
        Write("cache/plugins.json", "{}");
    }

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_defaultRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
