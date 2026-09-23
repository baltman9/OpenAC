using AcDream.Launcher.Core.Installation;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.ViewModels;
using AcDream.Platform;

namespace AcDream.Launcher.Tests;

public sealed partial class LauncherWindowViewModelTests
{
    /// <summary>
    /// A session reaching the world is what lets the old folders go, and a
    /// running one blocks Move…. Mutation: dropping the session note in
    /// RefreshFromCore, or ignoring active sessions in CanMoveInstallFolder,
    /// fails this.
    /// </summary>
    [Fact]
    public void SessionsDriveTheInstallFolderMoveAndOldFolderRemoval()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "openac-window-folder-" + Guid.NewGuid().ToString("N"));
        try
        {
            string root = Path.Combine(scratch, "OpenAC");
            ApplicationPathSet paths = ApplicationPathSet.ForRoot(root, ApplicationRootSource.Default);
            string old = Path.Combine(scratch, "acdream");
            Directory.CreateDirectory(old);
            File.WriteAllText(
                Path.Combine(old, InstallRootMigration.MovedNoteFileName),
                "moved" + Environment.NewLine + root + Environment.NewLine);
            InstallRootMigration.WriteLayoutMarker(paths, [old]);

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
            Assert.False(installFolder.CanRemoveOldFolders);

            orchestrator.Session = FakeLauncherOrchestrator.CreateSession(LauncherActivityState.InWorld, "In game.");
            orchestrator.RaiseStateChanged();
            Assert.True(installFolder.CanRemoveOldFolders);

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

    /// <summary>Mutation: not surfacing an incomplete migration as an error fails this.</summary>
    [Fact]
    public void AnIncompleteMigrationShowsAsTheWindowError()
    {
        using var orchestrator = new FakeLauncherOrchestrator();
        using var viewModel = new LauncherWindowViewModel(orchestrator, new ImmediateUiDispatcher());
        ApplicationPathSet paths = ApplicationPathSet.ForRoot(
            Path.Combine(Path.GetTempPath(), "openac-never-created-" + Guid.NewGuid().ToString("N")),
            ApplicationRootSource.Default);

        viewModel.ConfigureInstallFolder(new InstallFolderViewModel(
            paths,
            new InstallRootMover(paths, paths.RootDirectory),
            new ImmediateUiDispatcher(),
            () => true,
            () => { },
            new InstallRootMigrationResult(
                InstallRootMigrationOutcome.Incomplete,
                [],
                ["app: in use"],
                [])));

        Assert.True(viewModel.HasError);
        Assert.Contains("in use", viewModel.LastError, StringComparison.Ordinal);
    }
}
