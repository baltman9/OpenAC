using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;

namespace AcDream.Launcher.Tests;

public sealed partial class LauncherWindowViewModelTests
{
    /// <summary>
    /// A running session blocks Move…, and the install may move once it has
    /// exited. Mutation: ignoring active sessions in CanMoveInstallFolder
    /// fails this.
    /// </summary>
    [Fact]
    public void ARunningSessionBlocksMovingTheInstallFolder()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "openac-window-folder-" + Guid.NewGuid().ToString("N"));
        try
        {
            string root = Path.Combine(scratch, "OpenAC");
            ApplicationPathSet paths = ApplicationPathSet.ForRoot(root, ApplicationRootSource.Default);

            using var orchestrator = new FakeLauncherOrchestrator
            {
                Session = FakeLauncherOrchestrator.CreateSession(LauncherActivityState.Running, "Starting."),
            };
            using var viewModel = new LauncherWindowViewModel(orchestrator, new ImmediateUiDispatcher());
            viewModel.Initialize();
            var installFolder = new InstallFolderViewModel(
                paths,
                new InstallRootMover(paths, root),
                new ImmediateUiDispatcher(),
                () => viewModel.CanMoveInstallFolder,
                () => { });
            viewModel.ConfigureInstallFolder(installFolder);

            Assert.False(installFolder.CanMove);

            orchestrator.Session = FakeLauncherOrchestrator.CreateSession(LauncherActivityState.Exited, "Exited cleanly.");
            orchestrator.RaiseStateChanged();
            Assert.True(installFolder.CanMove);
        }
        finally
        {
            if (Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The first-run form says this is a new installation when an earlier
    /// version's folders exist, and not when there are none. Mutation:
    /// showing the notice without earlier folders, or never, fails this.
    /// </summary>
    [Fact]
    public void TheFirstRunFormSaysNewInstallationOnlyWhenEarlierFoldersExist()
    {
        ApplicationPathSet paths = ApplicationPathSet.ForRoot(
            Path.Combine(Path.GetTempPath(), "openac-never-created-" + Guid.NewGuid().ToString("N")),
            ApplicationRootSource.Default);

        using var freshOrchestrator = new FakeLauncherOrchestrator();
        using var fresh = new LauncherWindowViewModel(freshOrchestrator, new ImmediateUiDispatcher());
        fresh.ConfigureInstallFolder(CreateInstallFolder(paths, earlierFolders: []));

        using var upgradedOrchestrator = new FakeLauncherOrchestrator();
        using var upgraded = new LauncherWindowViewModel(upgradedOrchestrator, new ImmediateUiDispatcher());
        var changed = new List<string?>();
        upgraded.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        upgraded.ConfigureInstallFolder(CreateInstallFolder(
            paths,
            earlierFolders: [Path.Combine(Path.GetTempPath(), "acdream")]));

        Assert.False(fresh.ShowNewInstallationNotice);
        Assert.True(upgraded.ShowNewInstallationNotice);
        Assert.Contains(nameof(LauncherWindowViewModel.ShowNewInstallationNotice), changed);
    }

    private static InstallFolderViewModel CreateInstallFolder(
        ApplicationPathSet paths,
        IReadOnlyList<string> earlierFolders) =>
        new(
            paths,
            new InstallRootMover(paths, paths.RootDirectory),
            new ImmediateUiDispatcher(),
            () => true,
            () => { },
            earlierFolders);
}
