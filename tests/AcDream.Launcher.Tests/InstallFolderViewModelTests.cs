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
    /// new folder. Mutation: enabling removal before NotifyClientStarted fails this.
    /// </summary>
    [Fact]
    public void OldFoldersAreRemovableOnlyAfterAClientStarted()
    {
        string old = Path.Combine(_scratch, "Local", "acdream");
        Directory.CreateDirectory(old);
        File.WriteAllText(
            Path.Combine(old, InstallRootMigration.MovedNoteFileName),
            "moved" + Environment.NewLine + _defaultRoot + Environment.NewLine);
        InstallRootMigration.WriteLayoutMarker(_paths, [old]);
        InstallFolderViewModel viewModel = Create();

        Assert.Equal([old], viewModel.OldFolders);
        Assert.False(viewModel.RemoveOldFoldersCommand.CanExecute(null));

        viewModel.NotifyClientStarted();
        Assert.True(viewModel.RemoveOldFoldersCommand.CanExecute(null));
        viewModel.RemoveOldFoldersCommand.Execute(null);

        Assert.False(Directory.Exists(old));
        Assert.False(viewModel.HasOldFolders);
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
