using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;

namespace AcDream.Launcher.Tests;

public sealed class InstallFolderViewModelTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "openac-folder-vm-" + Guid.NewGuid().ToString("N"));

    private readonly string _defaultRoot;
    private readonly ApplicationPathSet _paths;
    private readonly FakeShell _shell = new();
    private int _restarts;

    public InstallFolderViewModelTests()
    {
        _defaultRoot = Path.Combine(_scratch, "Local", "OpenAC");
        _paths = ApplicationPathSet.ForRoot(_defaultRoot, ApplicationRootSource.Default);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratch))
            Directory.Delete(_scratch, recursive: true);
    }

    /// <summary>Mutation: pointing any row at another member fails this.</summary>
    [Fact]
    public void RowsNameTheRootAndTheFoldersAPlayerOpens()
    {
        InstallFolderViewModel viewModel = Create();

        Assert.Equal(_defaultRoot, viewModel.RootPath);
        Assert.Equal(_defaultRoot, viewModel.Root.Path);
        Assert.Equal(
            [
                ("Game", Path.Combine(_defaultRoot, "app")),
                ("Plugins", Path.Combine(_defaultRoot, "plugins")),
                ("Settings", Path.Combine(_defaultRoot, "settings")),
                ("Logs", Path.Combine(_defaultRoot, "logs")),
            ],
            viewModel.Folders.Select(row => (row.Label, row.Path)).ToArray());
    }

    /// <summary>
    /// A folder the system will not open offers its path to copy instead.
    /// Mutation: leaving OpenFailed false after a refused open fails this.
    /// </summary>
    [Fact]
    public async Task AFolderThatWillNotOpenOffersItsPathToCopy()
    {
        InstallFolderViewModel viewModel = Create();
        InstallFolderRowViewModel logs = viewModel.Folders.Single(row => row.Label == "Logs");

        await logs.OpenCommand.ExecuteAsync();
        Assert.False(logs.OpenFailed);
        Assert.Equal([logs.Path], _shell.Opened);

        _shell.OpenSucceeds = false;
        await logs.OpenCommand.ExecuteAsync();
        Assert.True(logs.OpenFailed);

        await logs.CopyPathCommand.ExecuteAsync();
        Assert.Equal(logs.Path, _shell.Copied);
    }

    /// <summary>Mutation: ignoring the can-move gate (a running session) fails this.</summary>
    [Fact]
    public async Task MoveIsRefusedWhileSomethingRuns()
    {
        Seed();
        InstallFolderViewModel viewModel = Create(canMove: false);

        await viewModel.MoveToAsync(Path.Combine(_scratch, "Games", "OpenAC"));

        Assert.Equal(InstallRootMover.SessionRefusal, viewModel.MoveError);
        Assert.Equal(0, _restarts);
        Assert.True(File.Exists(Path.Combine(_defaultRoot, "settings", "settings.json")));
    }

    /// <summary>Mutation: not restarting the launcher after a move fails this.</summary>
    [Fact]
    public async Task PickingAFolderMovesTheInstallAndRestartsTheLauncher()
    {
        Seed();
        string target = Path.Combine(_scratch, "Games", "OpenAC");
        _shell.Picked = target;
        InstallFolderViewModel viewModel = Create();

        await viewModel.MoveCommand.ExecuteAsync();

        Assert.Null(viewModel.MoveError);
        Assert.Equal(1, _restarts);
        Assert.True(File.Exists(Path.Combine(target, "settings", "settings.json")));
        Assert.Equal(InstallFolderViewModel.PickerTitle, _shell.PickerTitle);
    }

    /// <summary>Mutation: moving into a non-empty folder fails this.</summary>
    [Fact]
    public async Task AnOccupiedFolderIsRefusedWithAReason()
    {
        Seed();
        string occupied = Path.Combine(_scratch, "occupied");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "x"), "x");
        InstallFolderViewModel viewModel = Create();

        await viewModel.MoveToAsync(occupied);

        Assert.NotNull(viewModel.MoveError);
        Assert.Equal(0, _restarts);
    }

    /// <summary>
    /// Old folders are offered only after a client reached the world from the
    /// new folder, and deleted only after the player confirms a list with
    /// sizes. Mutation: enabling removal before NotifyClientStarted, or
    /// deleting on the first click, fails this.
    /// </summary>
    [Fact]
    public void OldFoldersAreRemovedOnlyAfterAClientStartedAndTheListIsConfirmed()
    {
        string old = SeedOldFolder(out string leftBehind);
        InstallFolderViewModel viewModel = Create();

        Assert.Equal([old], viewModel.OldFolders);
        Assert.False(viewModel.RemoveOldFoldersCommand.CanExecute(null));

        viewModel.NotifyClientStarted();
        viewModel.RemoveOldFoldersCommand.Execute(null);
        Assert.True(viewModel.IsReviewingOldFolderRemoval);
        Assert.True(Directory.Exists(old));
        Assert.Contains(viewModel.OldFolderRemovalItems, item => item.StartsWith(leftBehind, StringComparison.Ordinal) && item.EndsWith("(3 B)", StringComparison.Ordinal));

        viewModel.ConfirmRemoveOldFoldersCommand.Execute(null);

        Assert.False(Directory.Exists(old));
        Assert.False(viewModel.HasOldFolders);
    }

    /// <summary>
    /// A file that appeared in the old folder after the move blocks the
    /// removal and says why. Mutation: letting Delete run on a refused plan fails this.
    /// </summary>
    [Fact]
    public void ASurpriseInTheOldFolderBlocksRemoval()
    {
        string old = SeedOldFolder(out _);
        File.WriteAllText(Path.Combine(old, "late.log"), "x");
        InstallFolderViewModel viewModel = Create();
        viewModel.NotifyClientStarted();

        viewModel.RemoveOldFoldersCommand.Execute(null);

        Assert.False(viewModel.ConfirmRemoveOldFoldersCommand.CanExecute(null));
        Assert.Contains("late.log", viewModel.OldFolderRemovalSummary, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(old, "late.log")));
    }

    private string SeedOldFolder(out string leftBehind)
    {
        string old = Path.Combine(_scratch, "Local", "acdream");
        leftBehind = Path.Combine(old, "navigation");
        Directory.CreateDirectory(leftBehind);
        File.WriteAllText(Path.Combine(leftBehind, "x.nav"), "nav");
        File.WriteAllText(
            Path.Combine(old, InstallRootMigration.MovedNoteFileName),
            "moved" + Environment.NewLine + _defaultRoot + Environment.NewLine);
        InstallRootMigration.WriteLayoutMarker(_paths, [old], leftBehind: [leftBehind]);
        return old;
    }

    /// <summary>Mutation: describing an incomplete migration as done fails this.</summary>
    [Fact]
    public void AnIncompleteMigrationIsReportedAsSuch()
    {
        InstallFolderViewModel viewModel = Create(migration: new InstallRootMigrationResult(
            InstallRootMigrationOutcome.Incomplete,
            [],
            ["app\\0.1.15: in use"],
            []));

        Assert.True(viewModel.MigrationIncomplete);
        Assert.Contains("in use", viewModel.MigrationNotice, StringComparison.Ordinal);
    }

    /// <summary>
    /// Retry runs the migration again: still unfinished keeps sessions
    /// blocked, finished restarts the launcher. Mutation: restarting on an
    /// unfinished retry, or not restarting on a finished one, fails this.
    /// </summary>
    [Fact]
    public void RetryKeepsSessionsBlockedUntilTheMigrationFinishes()
    {
        var incomplete = new InstallRootMigrationResult(
            InstallRootMigrationOutcome.Incomplete, [], ["app: in use"], []);
        InstallRootMigrationResult next = incomplete;
        var viewModel = new InstallFolderViewModel(
            _paths,
            new InstallRootMover(_paths, _defaultRoot),
            new ImmediateUiDispatcher(),
            () => true,
            () => _restarts++,
            incomplete,
            () => next);

        Assert.True(viewModel.MigrationBlocksSessions);
        viewModel.RetryMigrationCommand.Execute(null);
        Assert.True(viewModel.MigrationBlocksSessions);
        Assert.Equal(0, _restarts);

        next = new InstallRootMigrationResult(InstallRootMigrationOutcome.Migrated, [], [], []);
        viewModel.RetryMigrationCommand.Execute(null);
        Assert.False(viewModel.MigrationBlocksSessions);
        Assert.Equal(1, _restarts);
    }

    /// <summary>Mutation: leaving conflicts out of the notice fails this.</summary>
    [Fact]
    public void ConflictsAreShownInTheMigrationNotice()
    {
        string kept = Path.Combine(_defaultRoot, "settings", "settings.json.from-old");
        InstallFolderViewModel viewModel = Create(migration: new InstallRootMigrationResult(
            InstallRootMigrationOutcome.Migrated,
            [],
            [],
            [])
        {
            Conflicts = [kept],
        });

        Assert.Contains(kept, viewModel.MigrationNotice, StringComparison.Ordinal);
        Assert.Contains(".from-old", viewModel.MigrationNotice, StringComparison.Ordinal);
    }

    private InstallFolderViewModel Create(
        bool canMove = true,
        InstallRootMigrationResult? migration = null)
    {
        var viewModel = new InstallFolderViewModel(
            _paths,
            new InstallRootMover(_paths, _defaultRoot, (_, _) => true),
            new ImmediateUiDispatcher(),
            () => canMove,
            () => _restarts++,
            migration);
        viewModel.AttachShell(_shell);
        return viewModel;
    }

    private void Seed()
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllText(_paths.SettingsFile, "{}");
    }

    private sealed class FakeShell : IInstallFolderShell
    {
        public bool OpenSucceeds { get; set; } = true;

        public List<string> Opened { get; } = [];

        public string? Copied { get; private set; }

        public string? Picked { get; set; }

        public string? PickerTitle { get; private set; }

        public Task<bool> OpenFolderAsync(string path)
        {
            Opened.Add(path);
            return Task.FromResult(OpenSucceeds);
        }

        public Task CopyTextAsync(string text)
        {
            Copied = text;
            return Task.CompletedTask;
        }

        public Task<string?> PickFolderAsync(string title)
        {
            PickerTitle = title;
            return Task.FromResult(Picked);
        }
    }
}
