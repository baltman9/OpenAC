using System.Collections.ObjectModel;
using AcDream.Launcher.Core.Installation;
using AcDream.Platform;

namespace AcDream.Launcher.ViewModels;

/// <summary>
/// What the install folder rows need from the window: opening a folder in
/// the system's file manager, the clipboard, and a folder picker.
/// </summary>
public interface IInstallFolderShell
{
    /// <summary>Opens <paramref name="path"/> in the file manager; false when the system would not.</summary>
    Task<bool> OpenFolderAsync(string path);

    /// <summary>Puts <paramref name="text"/> on the clipboard.</summary>
    Task CopyTextAsync(string text);

    /// <summary>Asks for a folder; null when the player cancelled.</summary>
    Task<string?> PickFolderAsync(string title);
}

/// <summary>One clickable folder in the launcher settings.</summary>
public sealed class InstallFolderRowViewModel : ObservableObject
{
    private readonly Func<IInstallFolderShell?> _shell;
    private bool _openFailed;

    internal InstallFolderRowViewModel(
        string label,
        string path,
        Func<IInstallFolderShell?> shell)
    {
        Label = label;
        Path = path;
        _shell = shell;
        OpenCommand = new AsyncRelayCommand(OpenAsync);
        CopyPathCommand = new AsyncRelayCommand(CopyAsync);
    }

    /// <summary>What the folder holds, in the player's words.</summary>
    public string Label { get; }

    /// <summary>The folder's full path.</summary>
    public string Path { get; }

    /// <summary>Opens the folder in the file manager.</summary>
    public AsyncRelayCommand OpenCommand { get; }

    /// <summary>Puts the folder's path on the clipboard.</summary>
    public AsyncRelayCommand CopyPathCommand { get; }

    /// <summary>True once opening the folder failed, so the row offers its path to copy instead.</summary>
    public bool OpenFailed
    {
        get => _openFailed;
        private set => SetProperty(ref _openFailed, value);
    }

    /// <summary>The line shown when the folder could not be opened.</summary>
    public string OpenFailedText => "Could not open this folder. Copy its path instead.";

    private async Task OpenAsync()
    {
        IInstallFolderShell? shell = _shell();
        bool opened = false;
        if (shell is not null)
        {
            try
            {
                // A folder nothing has written to yet still opens, empty.
                Directory.CreateDirectory(Path);
                opened = await shell.OpenFolderAsync(Path).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or InvalidOperationException
                                       or NotSupportedException)
            {
                opened = false;
            }
        }

        OpenFailed = !opened;
    }

    private async Task CopyAsync()
    {
        if (_shell() is { } shell)
            await shell.CopyTextAsync(Path).ConfigureAwait(true);
    }
}

/// <summary>
/// The install folder section of the launcher settings and the first-run
/// form: where everything lives, a row per folder the player may want to
/// open, moving the whole install, and the old folders a migration left.
/// </summary>
public sealed class InstallFolderViewModel : ObservableObject
{
    /// <summary>The title of the folder picker Move… opens.</summary>
    public const string PickerTitle = "Choose where OpenAC keeps its files";

    private readonly ApplicationPathSet _paths;
    private readonly InstallRootMover _mover;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<bool> _canMove;
    private readonly Action _restartLauncher;
    private IInstallFolderShell? _shell;
    private bool _isMoving;
    private string? _moveStatus;
    private string? _moveError;
    private bool _clientStarted;
    private IReadOnlyList<string> _oldFolders;
    private string? _oldFoldersStatus;
    private readonly Func<InstallRootMigrationResult>? _retryMigration;
    private string? _migrationNotice;
    private string? _migrationBlockReason;
    private OldRootRemovalPlan? _removalPlan;
    private bool _isReviewingOldFolderRemoval;
    private IReadOnlyList<string> _oldFolderRemovalItems = [];
    private string? _oldFolderRemovalSummary;

    /// <param name="paths">The install as this launcher resolved it.</param>
    /// <param name="mover">Moves the install.</param>
    /// <param name="dispatcher">Brings progress back to the window's thread.</param>
    /// <param name="canMove">False while a session runs or another operation is busy.</param>
    /// <param name="restartLauncher">Starts the launcher again once the install moved.</param>
    /// <param name="migration">What the startup migration did, if anything.</param>
    public InstallFolderViewModel(
        ApplicationPathSet paths,
        InstallRootMover mover,
        IUiDispatcher dispatcher,
        Func<bool> canMove,
        Action restartLauncher,
        InstallRootMigrationResult? migration = null,
        Func<InstallRootMigrationResult>? retryMigration = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _mover = mover ?? throw new ArgumentNullException(nameof(mover));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _canMove = canMove ?? throw new ArgumentNullException(nameof(canMove));
        _restartLauncher = restartLauncher ?? throw new ArgumentNullException(nameof(restartLauncher));
        Root = new InstallFolderRowViewModel("Install folder", paths.RootDirectory, () => _shell);
        Folders =
        [
            new InstallFolderRowViewModel("Game", paths.AppDirectory, () => _shell),
            new InstallFolderRowViewModel("Plugins", paths.PluginsDirectory, () => _shell),
            new InstallFolderRowViewModel("Settings", paths.ConfigDirectory, () => _shell),
            new InstallFolderRowViewModel("Logs", paths.LogsDirectory, () => _shell),
        ];
        _retryMigration = retryMigration;
        _migrationNotice = DescribeMigration(migration, paths.RootDirectory);
        _migrationBlockReason = migration is null
            ? null
            : InstallRootMigration.StartupBlockReason(migration, paths.RootDirectory);
        RetryMigrationCommand = new RelayCommand(
            RetryMigration,
            () => MigrationBlocksSessions && _retryMigration is not null);
        _oldFolders = InstallRootMigration.ReadOldRoots(paths);
        MoveCommand = new AsyncRelayCommand(PickAndMoveAsync, () => CanMove);
        RemoveOldFoldersCommand = new RelayCommand(ReviewOldFolderRemoval, () => CanRemoveOldFolders);
        ConfirmRemoveOldFoldersCommand = new RelayCommand(
            RemoveOldFolders,
            () => IsReviewingOldFolderRemoval && _removalPlan is { CanRemove: true });
        CancelRemoveOldFoldersCommand = new RelayCommand(
            () => IsReviewingOldFolderRemoval = false);
    }

    /// <summary>The install folder itself.</summary>
    public InstallFolderRowViewModel Root { get; }

    /// <summary>The folders inside it a player opens most.</summary>
    public ObservableCollection<InstallFolderRowViewModel> Folders { get; }

    /// <summary>The install folder's full path.</summary>
    public string RootPath => _paths.RootDirectory;

    /// <summary>Picks a folder and moves the install there.</summary>
    public AsyncRelayCommand MoveCommand { get; }

    /// <summary>Deletes the old per-user folders a migration left behind.</summary>
    public RelayCommand RemoveOldFoldersCommand { get; }

    /// <summary>Why the install cannot be moved from here, or null.</summary>
    public string? MoveUnavailableReason => _mover.UnavailableReason;

    /// <summary>Whether Move… can run now.</summary>
    public bool CanMove => !IsMoving && _mover.UnavailableReason is null && _canMove();

    /// <summary>True while the install is moving.</summary>
    public bool IsMoving
    {
        get => _isMoving;
        private set
        {
            if (SetProperty(ref _isMoving, value))
                NotifyCanMoveChanged();
        }
    }

    /// <summary>Progress or the outcome of the last move.</summary>
    public string? MoveStatus
    {
        get => _moveStatus;
        private set => SetProperty(ref _moveStatus, value);
    }

    /// <summary>Why the last move did not happen.</summary>
    public string? MoveError
    {
        get => _moveError;
        private set
        {
            if (SetProperty(ref _moveError, value))
                OnPropertyChanged(nameof(HasMoveError));
        }
    }

    /// <summary>Whether <see cref="MoveError"/> has something to show.</summary>
    public bool HasMoveError => !string.IsNullOrWhiteSpace(MoveError);

    /// <summary>What the startup migration did, or null when it did nothing worth saying.</summary>
    public string? MigrationNotice
    {
        get => _migrationNotice;
        private set
        {
            if (SetProperty(ref _migrationNotice, value))
                OnPropertyChanged(nameof(HasMigrationNotice));
        }
    }

    /// <summary>True when the startup migration could not move everything.</summary>
    public bool MigrationIncomplete => MigrationBlocksSessions;

    /// <summary>
    /// Why no session may start: the old folders are only half moved, or
    /// another process is still moving them. Null when sessions may start.
    /// </summary>
    public string? MigrationBlockReason
    {
        get => _migrationBlockReason;
        private set
        {
            if (SetProperty(ref _migrationBlockReason, value))
            {
                OnPropertyChanged(nameof(MigrationBlocksSessions));
                RetryMigrationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True while the migration keeps sessions from starting.</summary>
    public bool MigrationBlocksSessions => MigrationBlockReason is not null;

    /// <summary>Runs the migration again; restarts the launcher when it finishes.</summary>
    public RelayCommand RetryMigrationCommand { get; }

    /// <summary>Whether <see cref="MigrationNotice"/> has something to show.</summary>
    public bool HasMigrationNotice => !string.IsNullOrWhiteSpace(MigrationNotice);

    /// <summary>The old per-user folders a migration left in place.</summary>
    public IReadOnlyList<string> OldFolders
    {
        get => _oldFolders;
        private set
        {
            if (SetProperty(ref _oldFolders, value))
            {
                OnPropertyChanged(nameof(HasOldFolders));
                OnPropertyChanged(nameof(OldFoldersText));
                RemoveOldFoldersCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Whether there are old folders to offer for removal.</summary>
    public bool HasOldFolders => OldFolders.Count > 0;

    /// <summary>The old folders, one per line.</summary>
    public string OldFoldersText => string.Join(Environment.NewLine, OldFolders);

    /// <summary>
    /// Old folders are offered for removal only once a client has reached the
    /// world from the new folder, which is the proof nothing is still needed
    /// from them.
    /// </summary>
    public bool CanRemoveOldFolders => HasOldFolders && _clientStarted && !IsMoving;

    /// <summary>What removing the old folders did, or why it is not offered yet.</summary>
    public string? OldFoldersStatus
    {
        get => _oldFoldersStatus ?? (_clientStarted
            ? null
            : "Start a client from the new folder once; then the old folders can be removed.");
        private set => SetProperty(ref _oldFoldersStatus, value);
    }

    /// <summary>Deletes the reviewed old folders.</summary>
    public RelayCommand ConfirmRemoveOldFoldersCommand { get; }

    /// <summary>Closes the review without deleting anything.</summary>
    public RelayCommand CancelRemoveOldFoldersCommand { get; }

    /// <summary>True while the player reviews what removing the old folders deletes.</summary>
    public bool IsReviewingOldFolderRemoval
    {
        get => _isReviewingOldFolderRemoval;
        private set
        {
            if (SetProperty(ref _isReviewingOldFolderRemoval, value))
                ConfirmRemoveOldFoldersCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Each entry the removal deletes, with its size.</summary>
    public IReadOnlyList<string> OldFolderRemovalItems
    {
        get => _oldFolderRemovalItems;
        private set => SetProperty(ref _oldFolderRemovalItems, value);
    }

    /// <summary>The total, or why nothing can be removed.</summary>
    public string? OldFolderRemovalSummary
    {
        get => _oldFolderRemovalSummary;
        private set => SetProperty(ref _oldFolderRemovalSummary, value);
    }

    /// <summary>Gives the rows their window.</summary>
    public void AttachShell(IInstallFolderShell? shell) => _shell = shell;

    /// <summary>Called when a client first reaches the world in this launcher run.</summary>
    public void NotifyClientStarted()
    {
        if (_clientStarted)
            return;
        _clientStarted = true;
        OnPropertyChanged(nameof(CanRemoveOldFolders));
        OnPropertyChanged(nameof(OldFoldersStatus));
        RemoveOldFoldersCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Called when whether a move may run could have changed.</summary>
    public void NotifyCanMoveChanged()
    {
        OnPropertyChanged(nameof(CanMove));
        MoveCommand.NotifyCanExecuteChanged();
    }

    private async Task PickAndMoveAsync()
    {
        if (_shell is not { } shell)
            return;
        string? target = await shell.PickFolderAsync(PickerTitle).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(target))
            await MoveToAsync(target).ConfigureAwait(true);
    }

    /// <summary>Moves the install to <paramref name="target"/> and restarts the launcher on success.</summary>
    public async Task MoveToAsync(string target)
    {
        if (!CanMove)
        {
            MoveError = _mover.UnavailableReason ?? InstallRootMover.SessionRefusal;
            return;
        }

        if (_mover.ValidateTarget(target) is { } invalid)
        {
            MoveError = invalid;
            return;
        }

        MoveError = null;
        IsMoving = true;
        MoveStatus = "Moving the install folder…";
        InstallRootMoveResult result;
        try
        {
            result = await Task.Run(() => _mover.Move(
                    target,
                    entry => _dispatcher.Post(() => MoveStatus = $"Moving {entry}…")))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // This runs from a button: whatever went wrong is the player's to
            // read, not an unobserved failure that closes the launcher.
            MoveStatus = null;
            MoveError = "The install folder was not moved: " + ex.Message;
            return;
        }
        finally
        {
            IsMoving = false;
        }

        if (!result.Moved)
        {
            MoveStatus = null;
            MoveError = result.Message;
            return;
        }

        MoveStatus = result.Message + " The launcher restarts now.";
        _restartLauncher();
    }

    private void RetryMigration()
    {
        if (_retryMigration is null)
            return;

        InstallRootMigrationResult result = _retryMigration();
        MigrationNotice = DescribeMigration(result, _paths.RootDirectory);
        MigrationBlockReason = InstallRootMigration.StartupBlockReason(result, _paths.RootDirectory);
        if (MigrationBlockReason is null)
        {
            // Every store this launcher opened was opened on the half-moved
            // root; a fresh start reads the finished one.
            _restartLauncher();
        }
    }

    /// <summary>Asks before deleting: what would go, with sizes, or why nothing can.</summary>
    private void ReviewOldFolderRemoval()
    {
        _removalPlan = OldRootRemoval.Plan(_paths);
        OldFolderRemovalItems = _removalPlan.Items
            .Select(static item => $"{item.Path}  ({item.Size})")
            .ToArray();
        OldFolderRemovalSummary = _removalPlan.Refusals.Count > 0
            ? "Nothing can be removed yet: " + string.Join(" ", _removalPlan.Refusals.Take(3))
            : $"This deletes {_removalPlan.Items.Count} item(s), "
              + $"{OldRootRemovalItem.FormatBytes(_removalPlan.TotalBytes)} in all. "
              + "Nothing here is used by the new folder.";
        IsReviewingOldFolderRemoval = true;
    }

    private void RemoveOldFolders()
    {
        IsReviewingOldFolderRemoval = false;
        IReadOnlyList<string> failures = OldRootRemoval.Remove(_paths);
        OldFolders = InstallRootMigration.ReadOldRoots(_paths);
        OldFoldersStatus = failures.Count == 0
            ? "The old folders were removed."
            : "Some old files could not be removed: " + string.Join("; ", failures);
    }

    private static string? DescribeMigration(
        InstallRootMigrationResult? migration,
        string root)
    {
        string? outcome = DescribeOutcome(migration, root);
        if (migration is not { Conflicts.Count: > 0 })
            return outcome;

        string conflicts =
            $"{migration.Conflicts.Count} file(s) already existed in the new folder, so the old "
            + "copies were kept beside them with \".from-old\" added to the name: "
            + string.Join("; ", migration.Conflicts.Take(5))
            + (migration.Conflicts.Count > 5 ? "; …" : string.Empty);
        return outcome is null ? conflicts : outcome + " " + conflicts;
    }

    private static string? DescribeOutcome(
        InstallRootMigrationResult? migration,
        string root) => migration?.Outcome switch
    {
        InstallRootMigrationOutcome.Migrated =>
            $"Your settings, plugins and game files were moved into {root}.",
        InstallRootMigrationOutcome.Incomplete =>
            $"Some files could not be moved into {root} because they were in use. "
            + "Close every OpenAC client and restart the launcher to finish: "
            + string.Join("; ", migration.Failures.Take(3)),
        _ => null,
    };
}
