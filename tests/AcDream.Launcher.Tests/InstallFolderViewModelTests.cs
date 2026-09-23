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
    /// A player coming from an earlier version is told plainly that this is
    /// a new installation, that nothing came over, and where the old folders
    /// are; a player without earlier folders is told nothing. Mutation:
    /// dropping the folder list from the notice, or showing it without
    /// earlier folders, fails this.
    /// </summary>
    [Fact]
    public void EarlierFoldersMakeTheNewInstallationNoticeNameThem()
    {
        string roaming = Path.Combine(_scratch, "Roaming", "acdream");
        string local = Path.Combine(_scratch, "Local", "acdream");

        InstallFolderViewModel fresh = Create();
        InstallFolderViewModel upgraded = Create(earlierFolders: [roaming, local]);

        Assert.False(fresh.HasEarlierFolders);
        Assert.Null(fresh.NewInstallationNotice);
        Assert.True(upgraded.HasEarlierFolders);
        Assert.StartsWith("New installation.", upgraded.NewInstallationNotice, StringComparison.Ordinal);
        Assert.Contains("start fresh", upgraded.NewInstallationNotice, StringComparison.Ordinal);
        Assert.Contains(roaming, upgraded.NewInstallationNotice, StringComparison.Ordinal);
        Assert.Contains(local, upgraded.NewInstallationNotice, StringComparison.Ordinal);
        Assert.Equal(roaming + Environment.NewLine + local, upgraded.EarlierFoldersText);
    }

    /// <summary>
    /// A path the file system rejects is reported, never thrown out of the
    /// button. Mutation: letting ValidateTarget throw on it fails this.
    /// </summary>
    [Fact]
    public async Task AnUnusablePathIsReportedNotThrown()
    {
        InstallFolderViewModel viewModel = Create();

        await viewModel.MoveToAsync(Path.Combine(_scratch, "bad\0name"));

        Assert.NotNull(viewModel.MoveError);
        Assert.Equal(0, _restarts);
    }

    private InstallFolderViewModel Create(
        bool canMove = true,
        IReadOnlyList<string>? earlierFolders = null)
    {
        var viewModel = new InstallFolderViewModel(
            _paths,
            new InstallRootMover(_paths, _defaultRoot, (_, _) => true),
            new ImmediateUiDispatcher(),
            () => canMove,
            () => _restarts++,
            earlierFolders);
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
