using System.Collections.ObjectModel;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.ViewModels;

/// <summary>Who to enable a newly installed plugin for. Install itself never writes a character's
/// plugin list (L-300); only a non-<see cref="None"/> choice does, and only after install
/// succeeds.</summary>
public enum PluginEnableChoice
{
    None,
    All,
    Choose,
}

/// <summary>A character offered in the install dialog's "Choose" list.</summary>
public sealed record PluginCharacterOption(
    string ServerName,
    string AccountName,
    string CharacterName,
    string DisplayName);

public sealed class PluginCharacterChoiceViewModel(PluginCharacterOption option) : ObservableObject
{
    private bool _isChecked;

    public PluginCharacterOption Option { get; } = option;

    public string DisplayName => Option.DisplayName;

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

/// <summary>One capability chip on the install dialog: the author's claim, attributed rather than
/// verified (L-300).</summary>
public sealed class PluginCapabilityChipViewModel(LauncherPluginCapabilityDeclaration declaration)
{
    public string Label { get; } = DescribeLabel(declaration.Name);

    public string Note { get; } = declaration.Note;

    public string AutomationName { get; } = $"{DescribeLabel(declaration.Name)}: {declaration.Note}";

    private static string DescribeLabel(LauncherPluginCapability capability) => capability switch
    {
        LauncherPluginCapability.Network => "uses network",
        LauncherPluginCapability.Analytics => "collects analytics",
        LauncherPluginCapability.FileWrite => "writes outside",
        LauncherPluginCapability.ProcessLaunch => "starts programs",
        LauncherPluginCapability.NativeCode => "native code",
        LauncherPluginCapability.InputAutomation => "automates input",
        LauncherPluginCapability.Chat => "uses chat",
        _ => capability.ToString(),
    };
}

/// <summary>The overlay a Discover row's Install (or "Add from URL") opens, in the existing
/// <c>IsOpen</c> idiom. Repo URLs are shown as text only: nothing here opens a browser or runs
/// downloaded code (L-300).</summary>
public sealed class PluginInstallDialogViewModel : ObservableObject
{
    private readonly Func<bool> _canInteract;
    private Func<IReadOnlyList<LauncherPluginCapabilityDeclaration>, CancellationToken,
        Task<PluginInstallResult>>? _installAsync;
    private Action<string, IReadOnlyList<PluginCharacterOption>>? _enableForCharacters;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _capabilitiesCancellation;
    private IReadOnlyList<LauncherPluginCapabilityDeclaration> _displayedCapabilities = [];
    private bool _isOpen;
    private bool _isBusy;
    private bool _isUpdate;
    private bool _isLoadingCapabilities;
    private string? _capabilitiesLoadError;
    private PluginEnableChoice _choice = PluginEnableChoice.None;
    private string? _error;

    public PluginInstallDialogViewModel(Func<bool>? canInteract = null)
    {
        _canInteract = canInteract ?? (() => true);
        ConfirmCommand = new AsyncRelayCommand(
            ConfirmAsync,
            () => IsOpen && !IsBusy && !IsLoadingCapabilities && !HasCapabilitiesLoadError && _canInteract());
        CancelCommand = new RelayCommand(Close, () => !IsBusy);
    }

    public string Repo { get; private set; } = string.Empty;

    public string PluginId { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public bool IsListed { get; private set; }

    /// <summary>Whether the dialog opened for an already-installed plugin's Update; the enable
    /// choice is offered only on a first install (plan).</summary>
    public bool IsUpdate
    {
        get => _isUpdate;
        private set
        {
            if (SetProperty(ref _isUpdate, value))
            {
                OnPropertyChanged(nameof(ShowEnableChoice));
            }
        }
    }

    public bool ShowEnableChoice => !IsUpdate;

    /// <summary>The release version this dialog offers, carried from whichever caller resolved it
    /// (the update check, Discover's own details, or Add from URL), so the notice can name a
    /// pre-release offer (L-319).</summary>
    public string? OfferedVersion { get; private set; }

    public bool IsOfferedPrerelease =>
        OfferedVersion is not null
        && LauncherVersion.TryParse(OfferedVersion, out LauncherVersion? version)
        && version.IsPreRelease;

    /// <summary>The responsibility notice every install and update dialog shows, every time
    /// (L-313): no wording here says or implies OpenAC reviews plugins, listed or not.</summary>
    public string WarningText
    {
        get
        {
            string notice = IsListed
                ? "Plugins are made by third parties, not OpenAC. Installing one is your choice and your "
                    + "responsibility. Only install plugins from authors you trust."
                : "Plugins are made by third parties, not OpenAC. Installing one is your choice and your "
                    + "responsibility. Only install plugins from authors you trust.\n"
                    + "This plugin is not on the OpenAC plugin list.";
            return IsOfferedPrerelease ? notice + "\nThis version is a pre-release." : notice;
        }
    }

    public ObservableCollection<PluginCharacterChoiceViewModel> Characters { get; } = [];

    public bool HasCharacters => Characters.Count > 0;

    /// <summary>Every capability the manifest declared, in the launcher's own vocabulary order
    /// rather than the manifest's, so an author cannot bury one behind benign entries. Empty when
    /// the plugin declares none, which the dialog shows as nothing rather than a "none" line.</summary>
    public ObservableCollection<PluginCapabilityChipViewModel> Capabilities { get; } = [];

    public bool HasCapabilities => Capabilities.Count > 0;

    /// <summary>Attributes the chips to the author, since nothing here verifies the claim.</summary>
    public string CapabilitiesHeading => "The author says this plugin:";

    /// <summary>Whether the dialog is fetching a Discover row's manifest because Install was pressed
    /// before the background fetch filled it in: shown in place of the capability list, with Install
    /// disabled, so the dialog never opens looking like the plugin declares nothing.</summary>
    public bool IsLoadingCapabilities
    {
        get => _isLoadingCapabilities;
        private set
        {
            if (SetProperty(ref _isLoadingCapabilities, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string LoadingCapabilitiesText => "Checking what this plugin does…";

    public string? CapabilitiesLoadError
    {
        get => _capabilitiesLoadError;
        private set
        {
            if (SetProperty(ref _capabilitiesLoadError, value))
            {
                OnPropertyChanged(nameof(HasCapabilitiesLoadError));
                NotifyCommandStates();
            }
        }
    }

    public bool HasCapabilitiesLoadError => !string.IsNullOrWhiteSpace(CapabilitiesLoadError);

    public bool IsOpen
    {
        get => _isOpen;
        private set
        {
            if (SetProperty(ref _isOpen, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public PluginEnableChoice Choice
    {
        get => _choice;
        private set
        {
            if (SetProperty(ref _choice, value))
            {
                OnPropertyChanged(nameof(EnableNone));
                OnPropertyChanged(nameof(EnableAll));
                OnPropertyChanged(nameof(EnableChoose));
                OnPropertyChanged(nameof(IsChooseListVisible));
            }
        }
    }

    public bool EnableNone
    {
        get => Choice == PluginEnableChoice.None;
        set { if (value) Choice = PluginEnableChoice.None; }
    }

    public bool EnableAll
    {
        get => Choice == PluginEnableChoice.All;
        set { if (value) Choice = PluginEnableChoice.All; }
    }

    public bool EnableChoose
    {
        get => Choice == PluginEnableChoice.Choose;
        set { if (value) Choice = PluginEnableChoice.Choose; }
    }

    public bool IsChooseListVisible => Choice == PluginEnableChoice.Choose;

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public AsyncRelayCommand ConfirmCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Opens the dialog for a resolved repo/manifest. The caller supplies the actual
    /// network install and the character-enable write, so this view model stays testable without
    /// either one. <paramref name="capabilities"/> is the declared list when already known;
    /// <see langword="null"/> means it still needs fetching, and <paramref name="loadCapabilities"/>
    /// is the fetch to run for it. The dialog never opens showing an unknown list as an empty one.</summary>
    public void Open(
        string repo,
        string pluginId,
        string displayName,
        bool isListed,
        bool isUpdate,
        string? offeredVersion,
        IReadOnlyList<PluginCharacterOption> characters,
        Func<IReadOnlyList<LauncherPluginCapabilityDeclaration>, CancellationToken, Task<PluginInstallResult>> installAsync,
        Action<string, IReadOnlyList<PluginCharacterOption>> enableForCharacters,
        IReadOnlyList<LauncherPluginCapabilityDeclaration>? capabilities = null,
        Func<CancellationToken, Task<IReadOnlyList<LauncherPluginCapabilityDeclaration>>>? loadCapabilities = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(characters);
        Repo = repo;
        PluginId = pluginId;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? pluginId : displayName;
        IsListed = isListed;
        IsUpdate = isUpdate;
        OfferedVersion = offeredVersion;
        _installAsync = installAsync ?? throw new ArgumentNullException(nameof(installAsync));
        _enableForCharacters = enableForCharacters
            ?? throw new ArgumentNullException(nameof(enableForCharacters));

        Characters.Clear();
        foreach (PluginCharacterOption option in characters)
        {
            Characters.Add(new PluginCharacterChoiceViewModel(option));
        }

        _capabilitiesCancellation?.Cancel();
        _capabilitiesCancellation = null;
        CapabilitiesLoadError = null;
        if (capabilities is not null)
        {
            IsLoadingCapabilities = false;
            SetCapabilities(capabilities);
        }
        else if (loadCapabilities is not null)
        {
            SetCapabilities([]);
            IsLoadingCapabilities = true;
            _ = LoadCapabilitiesAsync(loadCapabilities);
        }
        else
        {
            IsLoadingCapabilities = false;
            SetCapabilities([]);
        }

        Choice = PluginEnableChoice.None;
        Error = null;
        OnPropertyChanged(nameof(Repo));
        OnPropertyChanged(nameof(PluginId));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(IsListed));
        OnPropertyChanged(nameof(OfferedVersion));
        OnPropertyChanged(nameof(IsOfferedPrerelease));
        OnPropertyChanged(nameof(WarningText));
        OnPropertyChanged(nameof(HasCharacters));
        IsOpen = true;
    }

    public void Close()
    {
        if (IsBusy)
        {
            return;
        }

        _cancellation?.Cancel();
        _capabilitiesCancellation?.Cancel();
        IsOpen = false;
    }

    private void SetCapabilities(IReadOnlyList<LauncherPluginCapabilityDeclaration> capabilities)
    {
        _displayedCapabilities = capabilities;
        Capabilities.Clear();
        foreach (LauncherPluginCapabilityDeclaration declaration in capabilities
                     .OrderBy(declaration => (int)declaration.Name))
        {
            Capabilities.Add(new PluginCapabilityChipViewModel(declaration));
        }

        OnPropertyChanged(nameof(HasCapabilities));
    }

    /// <summary>Runs a Discover row's on-demand manifest fetch (<c>Open</c>'s
    /// <paramref name="loadCapabilities"/>) and applies its outcome, guarding against a stale run
    /// finishing after the dialog has moved on to a different plugin.</summary>
    private async Task LoadCapabilitiesAsync(
        Func<CancellationToken, Task<IReadOnlyList<LauncherPluginCapabilityDeclaration>>> loadCapabilities)
    {
        var cancellation = new CancellationTokenSource();
        _capabilitiesCancellation = cancellation;
        try
        {
            IReadOnlyList<LauncherPluginCapabilityDeclaration> capabilities =
                await loadCapabilities(cancellation.Token).ConfigureAwait(true);
            if (ReferenceEquals(_capabilitiesCancellation, cancellation))
            {
                SetCapabilities(capabilities);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_capabilitiesCancellation, cancellation))
            {
                CapabilitiesLoadError = string.IsNullOrWhiteSpace(ex.Message)
                    ? "This plugin's details could not be checked."
                    : ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_capabilitiesCancellation, cancellation))
            {
                _capabilitiesCancellation = null;
                IsLoadingCapabilities = false;
            }
        }
    }

    private async Task ConfirmAsync()
    {
        if (IsBusy || _installAsync is null || _enableForCharacters is null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        Error = null;
        try
        {
            // Install succeeded once this returns: close the dialog before touching character
            // profiles, so a slow or failing enable step can never leave Confirm sitting there to
            // be pressed again and run the install a second time.
            PluginInstallResult result = await _installAsync(_displayedCapabilities, cancellation.Token)
                .ConfigureAwait(true);
            IsOpen = false;
            if (Choice != PluginEnableChoice.None)
            {
                PluginCharacterOption[] chosen = Choice == PluginEnableChoice.All
                    ? [.. Characters.Select(character => character.Option)]
                    : [.. Characters.Where(character => character.IsChecked)
                        .Select(character => character.Option)];
                if (chosen.Length > 0)
                {
                    _enableForCharacters(result.Id, chosen);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Error = "Install cancelled.";
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message) ? "The install failed." : ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                _cancellation = null;
            }

            IsBusy = false;
        }
    }

    private void NotifyCommandStates()
    {
        ConfirmCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }
}
