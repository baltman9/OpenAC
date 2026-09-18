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
/// verified (L-300). <paramref name="isNew"/> marks a capability an update adds beyond what the
/// installed version already declared, so a player skimming the list sees what changed.</summary>
public sealed class PluginCapabilityChipViewModel(
    LauncherPluginCapabilityDeclaration declaration, bool isNew = false)
{
    public string Label { get; } = isNew
        ? $"{DescribeLabel(declaration.Name)} (new)"
        : DescribeLabel(declaration.Name);

    public string Note { get; } = declaration.Note;

    public bool IsNew { get; } = isNew;

    public string AutomationName { get; } = isNew
        ? $"{DescribeLabel(declaration.Name)} (new): {declaration.Note}"
        : $"{DescribeLabel(declaration.Name)}: {declaration.Note}";

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
    private Action<string>? _disableForAllCharacters;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _capabilitiesCancellation;
    private IReadOnlyList<LauncherPluginCapabilityDeclaration> _displayedCapabilities = [];
    private IReadOnlyList<LauncherPluginCapabilityDeclaration>? _installedCapabilities;
    private bool _isOpen;
    private bool _isBusy;
    private bool _isUpdate;
    private bool _isLoadingCapabilities;
    private string? _capabilitiesLoadError;
    private bool _capabilitiesChanged;
    private IReadOnlyList<string> _affectedCharacters = [];
    private bool _keepEnabled;
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
                OnPropertyChanged(nameof(ShowKeepEnabledChoice));
                OnPropertyChanged(nameof(ConfirmLabel));
                OnPropertyChanged(nameof(BusyLabel));
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

    /// <summary>Whether the update's capabilities differ from the installed version's (any addition,
    /// removal, or reworded note; <see cref="PluginInstaller.CapabilitiesMatch"/> is the
    /// yardstick), computed once the displayed list is known. Never true for a first install, since
    /// there is nothing installed yet to differ from.</summary>
    public bool CapabilitiesChanged
    {
        get => _capabilitiesChanged;
        private set
        {
            if (SetProperty(ref _capabilitiesChanged, value))
            {
                OnPropertyChanged(nameof(ShowKeepEnabledChoice));
                OnPropertyChanged(nameof(ConfirmLabel));
            }
        }
    }

    /// <summary>The characters this plugin is already enabled for, named so the player knows who is
    /// affected by <see cref="ShowKeepEnabledChoice"/>'s default of turning it off.</summary>
    public IReadOnlyList<string> AffectedCharacters
    {
        get => _affectedCharacters;
        private set
        {
            if (SetProperty(ref _affectedCharacters, value))
            {
                OnPropertyChanged(nameof(HasAffectedCharacters));
                OnPropertyChanged(nameof(AffectedCharactersText));
                OnPropertyChanged(nameof(ShowKeepEnabledChoice));
            }
        }
    }

    public bool HasAffectedCharacters => AffectedCharacters.Count > 0;

    public string AffectedCharactersText =>
        $"Currently enabled for: {string.Join(", ", AffectedCharacters)}.";

    /// <summary>Shown only when an update changes capabilities for a plugin some character already
    /// has enabled: the new capabilities apply to that character unless the player opts in here, so
    /// this is the one place that choice is made.</summary>
    public bool ShowKeepEnabledChoice => IsUpdate && CapabilitiesChanged && HasAffectedCharacters;

    /// <summary>Defaults to off: an update that adds a capability does not carry the old consent
    /// forward, so a character already enabled loses the plugin unless the player ticks this.</summary>
    public bool KeepEnabled
    {
        get => _keepEnabled;
        set => SetProperty(ref _keepEnabled, value);
    }

    public string ConfirmLabel => IsUpdate && CapabilitiesChanged ? "Update and allow" : "Install";

    /// <summary>What the confirm button says while the work is actually running: a download and
    /// unzip over a slow link or a large plugin takes long enough that a dead button reads as a
    /// hang. Present tense, matching the launcher's other in-flight wording.</summary>
    public string BusyLabel => IsUpdate ? "Updating…" : "Installing…";

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
    /// is the fetch to run for it. The dialog never opens showing an unknown list as an empty one.
    /// <paramref name="installedCapabilities"/>, supplied only for an update, is what the installed
    /// version already declared: it is what <see cref="CapabilitiesChanged"/> and each chip's "new"
    /// mark are measured against. <paramref name="affectedCharacters"/> names who already has the
    /// plugin enabled, for <see cref="ShowKeepEnabledChoice"/>; <paramref name="disableForAllCharacters"/>
    /// is run on confirm when that choice is left unticked.</summary>
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
        Func<CancellationToken, Task<IReadOnlyList<LauncherPluginCapabilityDeclaration>>>? loadCapabilities = null,
        IReadOnlyList<LauncherPluginCapabilityDeclaration>? installedCapabilities = null,
        IReadOnlyList<string>? affectedCharacters = null,
        Action<string>? disableForAllCharacters = null)
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
        _disableForAllCharacters = disableForAllCharacters;
        _installedCapabilities = installedCapabilities;
        KeepEnabled = false;
        AffectedCharacters = affectedCharacters ?? [];

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
        HashSet<LauncherPluginCapability>? installedNames = _installedCapabilities?
            .Select(declaration => declaration.Name).ToHashSet();
        Capabilities.Clear();
        foreach (LauncherPluginCapabilityDeclaration declaration in capabilities
                     .OrderBy(declaration => (int)declaration.Name))
        {
            bool isNew = installedNames is not null && !installedNames.Contains(declaration.Name);
            Capabilities.Add(new PluginCapabilityChipViewModel(declaration, isNew));
        }

        OnPropertyChanged(nameof(HasCapabilities));
        CapabilitiesChanged = _installedCapabilities is not null
            && !PluginInstaller.CapabilitiesMatch(_installedCapabilities, capabilities);
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

            // The old consent never covered a capability this update adds: leaving every character
            // that already had it enabled would carry that consent forward silently, so it comes
            // off everywhere unless the player ticked Keep enabled.
            if (ShowKeepEnabledChoice && !KeepEnabled)
            {
                _disableForAllCharacters?.Invoke(result.Id);
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
