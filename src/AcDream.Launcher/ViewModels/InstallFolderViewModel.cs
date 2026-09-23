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
/// open, moving the whole install, and the folders an earlier version used.
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

    /// <param name="paths">The install as this launcher resolved it.</param>
    /// <param name="mover">Moves the install.</param>
    /// <param name="dispatcher">Brings progress back to the window's thread.</param>
    /// <param name="canMove">False while a session runs or another operation is busy.</param>
    /// <param name="restartLauncher">Starts the launcher again once the install moved.</param>
    /// <param name="earlierFolders">
    /// The folders a version from before the single install folder used, where
    /// they still exist. Nothing is brought over from them.
    /// </param>
    public InstallFolderViewModel(
        ApplicationPathSet paths,
        InstallRootMover mover,
        IUiDispatcher dispatcher,
        Func<bool> canMove,
        Action restartLauncher,
        IReadOnlyList<string>? earlierFolders = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _mover = mover ?? throw new ArgumentNullException(nameof(mover));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _canMove = canMove ?? throw new ArgumentNullException(nameof(canMove));
        _restartLauncher = restartLauncher ?? throw new ArgumentNullException(nameof(restartLauncher));
        EarlierFolders = earlierFolders ?? [];
        Root = new InstallFolderRowViewModel("Install folder", paths.RootDirectory, () => _shell);
        Folders =
        [
            new InstallFolderRowViewModel("Game", paths.AppDirectory, () => _shell),
            new InstallFolderRowViewModel("Plugins", paths.PluginsDirectory, () => _shell),
            new InstallFolderRowViewModel("Settings", paths.ConfigDirectory, () => _shell),
            new InstallFolderRowViewModel("Logs", paths.LogsDirectory, () => _shell),
        ];
        MoveCommand = new AsyncRelayCommand(PickAndMoveAsync, () => CanMove);
    }

    /// <summary>The install folder itself.</summary>
    public InstallFolderRowViewModel Root { get; }

    /// <summary>The folders inside it a player opens most.</summary>
    public ObservableCollection<InstallFolderRowViewModel> Folders { get; }

    /// <summary>The install folder's full path.</summary>
    public string RootPath => _paths.RootDirectory;

    /// <summary>Picks a folder and moves the install there.</summary>
    public AsyncRelayCommand MoveCommand { get; }

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

    /// <summary>
    /// The folders an earlier version, from before the single install folder,
    /// used and that still exist. This install never reads them.
    /// </summary>
    public IReadOnlyList<string> EarlierFolders { get; }

    /// <summary>Whether an earlier version's folders are still on this computer.</summary>
    public bool HasEarlierFolders => EarlierFolders.Count > 0;

    /// <summary>The earlier folders, one per line.</summary>
    public string EarlierFoldersText => string.Join(Environment.NewLine, EarlierFolders);

    /// <summary>
    /// What the first-run form tells a player who used an earlier version:
    /// this is a new installation, nothing came over, and the old folders
    /// are theirs to delete. Null when there were no earlier folders.
    /// </summary>
    public string? NewInstallationNotice => HasEarlierFolders
        ? "This is a new installation. OpenAC now keeps all its files in one folder, "
          + $"{RootPath}, and nothing was copied from the earlier version: launcher "
          + "settings, account profiles and plugins start fresh, so add your accounts and "
          + "install your plugins again. The earlier version's folders were left untouched "
          + "and are no longer used; delete them yourself once you no longer need anything "
          + "in them:"
          + Environment.NewLine
          + EarlierFoldersText
        : null;

    /// <summary>Gives the rows their window.</summary>
    public void AttachShell(IInstallFolderShell? shell) => _shell = shell;

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
}
