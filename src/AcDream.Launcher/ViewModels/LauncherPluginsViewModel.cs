using System.Collections.ObjectModel;
using System.Text;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.ViewModels;

/// <summary>The letters on a plugin card's tile until plugins can ship an icon: the first letters of
/// the first two words, or of the first two capitalised parts of a single word ("BuffBot" is BB).</summary>
internal static class PluginMonogram
{
    public static string From(string name)
    {
        string[] words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return "?";
        if (words.Length > 1)
            return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]));

        string word = words[0];
        for (int i = 1; i < word.Length; i++)
        {
            if (char.IsUpper(word[i]))
                return string.Concat(char.ToUpperInvariant(word[0]), word[i]);
        }

        return char.ToUpperInvariant(word[0]).ToString();
    }
}

/// <summary>One listed plugin not yet installed, shown on the Discover list.</summary>
public sealed class PluginDiscoverRowViewModel(
    string id,
    string name,
    string author,
    string description,
    string repo,
    RelayCommand installCommand)
    : ObservableObject
{
    private string? _latestVersion;
    private string? _compatibility;
    private bool _compatibilityIsWarning;
    private IReadOnlyList<LauncherPluginCapabilityDeclaration> _capabilities = [];

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Author { get; } = author;
    public string Description { get; } = description;
    public string Repo { get; } = repo;
    public string AuthorAndRepo { get; } = $"by {author} · {repo}";
    public string Initials { get; } = PluginMonogram.From(name);
    public string InstallAutomationName { get; } = $"Install {name}";
    public RelayCommand InstallCommand { get; } = installCommand;

    /// <summary>Filled in once the Plugins tab is opened (plan, "Request budget"): blank until
    /// then, so opening Discover costs one request per listed, not-installed plugin rather than
    /// every Check pass paying for plugins nobody is looking at.</summary>
    public string? LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (SetProperty(ref _latestVersion, value))
            {
                OnPropertyChanged(nameof(HasLatestVersion));
            }
        }
    }

    public bool HasLatestVersion => !string.IsNullOrWhiteSpace(LatestVersion);

    public string? Compatibility
    {
        get => _compatibility;
        set
        {
            if (SetProperty(ref _compatibility, value))
            {
                OnPropertyChanged(nameof(HasCompatibilityNote));
                OnPropertyChanged(nameof(ShowCompatibilityWarning));
                OnPropertyChanged(nameof(ShowCompatibilityMuted));
            }
        }
    }

    public bool HasCompatibilityNote => !string.IsNullOrWhiteSpace(Compatibility);

    public bool CompatibilityIsWarning
    {
        get => _compatibilityIsWarning;
        set
        {
            if (SetProperty(ref _compatibilityIsWarning, value))
            {
                OnPropertyChanged(nameof(ShowCompatibilityWarning));
                OnPropertyChanged(nameof(ShowCompatibilityMuted));
            }
        }
    }

    public bool ShowCompatibilityWarning => HasCompatibilityNote && CompatibilityIsWarning;
    public bool ShowCompatibilityMuted => HasCompatibilityNote && !CompatibilityIsWarning;
    public string CompatibilityText => LauncherPluginCompatibility.WithoutClientVersion(Compatibility ?? string.Empty);
    public bool ShowCompatibilityInfo => ShowCompatibilityMuted
        && !Compatibility!.StartsWith(LauncherPluginCompatibility.CompatiblePrefix, StringComparison.Ordinal);

    /// <summary>Filled in alongside <see cref="Compatibility"/>: a count only, never the claims
    /// themselves, since browsing is not a consent surface.</summary>
    public IReadOnlyList<LauncherPluginCapabilityDeclaration> Capabilities
    {
        get => _capabilities;
        set
        {
            if (SetProperty(ref _capabilities, value))
            {
                OnPropertyChanged(nameof(HasCapabilities));
                OnPropertyChanged(nameof(CapabilityCountText));
            }
        }
    }

    public bool HasCapabilities => Capabilities.Count > 0;

    public string CapabilityCountText => Capabilities.Count switch
    {
        0 => string.Empty,
        1 => "1 capability",
        var count => $"{count} capabilities",
    };
}

/// <summary>One installed plugin, shown on the Installed list.</summary>
public sealed class PluginInstalledRowViewModel(
    string id,
    string displayName,
    string version,
    string sourceBadge,
    string compatibility,
    bool compatibilityIsWarning,
    string? blocked,
    bool conflict,
    bool canRemove,
    bool showBetaToggle,
    bool isBetaChannel,
    bool isPrerelease,
    bool updateAvailable,
    string? updateVersion,
    string? updateCompatibilityNote,
    bool updateCompatibilityIsWarning,
    string? updateWithheldReason,
    RelayCommand? updateCommand,
    RelayCommand? removeCommand,
    string? refusal,
    bool hasDuplicate,
    Action<bool> onBetaToggled,
    Func<bool> canToggleBeta,
    IReadOnlyList<LauncherPluginCapabilityDeclaration> capabilities)
    : ObservableObject
{
    private bool _isBetaChannel = isBetaChannel;

    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;
    public string Version { get; } = version;
    public string SourceBadge { get; } = sourceBadge;
    public string Summary { get; } = $"{id} · v{version} · {sourceBadge}";
    public string Initials { get; } = PluginMonogram.From(displayName);
    public string Compatibility { get; } = compatibility;
    public bool HasCompatibilityNote => !string.IsNullOrWhiteSpace(Compatibility);
    public bool CompatibilityIsWarning { get; } = compatibilityIsWarning;
    public bool ShowCompatibilityWarning => HasCompatibilityNote && CompatibilityIsWarning;
    public bool ShowCompatibilityMuted => HasCompatibilityNote && !CompatibilityIsWarning;
    public string CompatibilityText => LauncherPluginCompatibility.WithoutClientVersion(Compatibility ?? string.Empty);
    public bool ShowCompatibilityInfo => ShowCompatibilityMuted
        && !Compatibility!.StartsWith(LauncherPluginCompatibility.CompatiblePrefix, StringComparison.Ordinal);
    public string? Blocked { get; } = blocked;
    public bool IsBlocked => !string.IsNullOrWhiteSpace(Blocked);
    public string? BlockedText => IsBlocked ? $"Blocked: {Blocked}" : null;
    public bool Conflict { get; } = conflict;
    public bool CanRemove { get; } = canRemove;
    public string? Refusal { get; } = refusal;
    public bool IsRefused => !string.IsNullOrWhiteSpace(Refusal);
    public string? RefusedText => IsRefused ? $"Refused: {Refusal}" : null;
    public bool HasDuplicate { get; } = hasDuplicate;
    // A refused plugin never loads, so its refusal takes the version line over a compatibility note.
    public bool ShowSkipped => ShowCompatibilityWarning && !IsRefused;
    public bool ShowCompatibilityInfoLine => ShowCompatibilityInfo && !IsRefused;
    public bool UpdateAvailable { get; } = updateAvailable;
    public string? UpdateVersion { get; } = updateVersion;
    public bool HasUpdateChip => UpdateAvailable && !string.IsNullOrWhiteSpace(UpdateVersion);
    public string? UpdateChipText => HasUpdateChip ? $"Update available: v{UpdateVersion}" : null;
    public string? UpdateCompatibilityNote { get; } = updateCompatibilityNote;
    public bool UpdateCompatibilityIsWarning { get; } = updateCompatibilityIsWarning;
    // The row's own Compatibility already covers the installed release; this only adds a line
    // when the newer one reads differently, so an unchanged note is never shown twice.
    public bool ShowUpdateCompatibilityNote => HasUpdateChip
        && !string.IsNullOrWhiteSpace(UpdateCompatibilityNote)
        && !string.Equals(UpdateCompatibilityNote, Compatibility, StringComparison.Ordinal);
    public string? UpdateWithheldReason { get; } = updateWithheldReason;
    public bool HasUpdateWithheldReason => !UpdateAvailable && !string.IsNullOrWhiteSpace(UpdateWithheldReason);
    public bool HasNotes => ShowUpdateCompatibilityNote || Conflict || HasUpdateWithheldReason;
    public bool HasChips => IsPrerelease || HasCapabilities || HasUpdateChip || HasDuplicate || IsBlocked;
    public string UpdateAutomationName { get; } = $"Update {displayName}";
    public string RemoveAutomationName { get; } = $"Remove {displayName}";
    public RelayCommand? UpdateCommand { get; } = updateCommand;
    public RelayCommand? RemoveCommand { get; } = removeCommand;

    /// <summary>Launcher-managed only (L-319): Direct and Bundled plugins have no channel.</summary>
    public bool ShowBetaToggle { get; } = showBetaToggle;

    public string BetaToggleAutomationName { get; } = $"Beta updates for {displayName}";

    /// <summary>Whether the installed release itself carries a SemVer prerelease part, regardless
    /// of the plugin's channel (a beta-channel plugin reads stable most of the time, per L-319).</summary>
    public bool IsPrerelease { get; } = isPrerelease;

    public bool IsBetaChannel
    {
        get => _isBetaChannel;
        set
        {
            if (!SetProperty(ref _isBetaChannel, value))
            {
                return;
            }

            onBetaToggled(value);
        }
    }

    public bool IsBetaToggleEnabled => canToggleBeta();

    internal void NotifyBetaToggleEnabledChanged() => OnPropertyChanged(nameof(IsBetaToggleEnabled));

    /// <summary>Puts the toggle back without re-triggering <c>onBetaToggled</c>, for a refused
    /// channel write (the barrier's lease refusal, shown the way Remove shows it).</summary>
    internal void RevertBetaChannel(bool value)
    {
        if (_isBetaChannel == value)
        {
            return;
        }

        _isBetaChannel = value;
        OnPropertyChanged(nameof(IsBetaChannel));
    }

    public IReadOnlyList<LauncherPluginCapabilityDeclaration> Capabilities { get; } = capabilities;
    public bool HasCapabilities => Capabilities.Count > 0;
    public string CapabilityCountText => Capabilities.Count switch
    {
        0 => string.Empty,
        1 => "1 capability",
        var count => $"{count} capabilities",
    };
}

/// <summary>Discover/Installed, Refresh list, Add from URL, and the install and remove dialogs
/// (plan, "MainWindow.axaml and view models"). Repo URLs are text only; nothing here opens a
/// browser, loads an assembly, or starts a process (L-300).</summary>
public sealed class LauncherPluginsViewModel : ObservableObject
{
    private readonly ILauncherOrchestrator _orchestrator;
    private readonly Func<bool> _canInteract;

    private LauncherPluginComposition? _composition;
    private Func<ClientVersionResolution?> _clientVersionResolver = () => null;
    private CancellationTokenSource? _cancellation;
    private DateTimeOffset? _listAgeUtc;
    private readonly Dictionary<string, DiscoverDetails> _discoverDetailsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _isBusy;
    private string? _error;
    private string? _statusText;
    private bool _isRateLimited;
    private bool _showBetaPlugins;
    private string _addFromUrlText = string.Empty;
    private bool _isRemoveDialogOpen;
    private InstalledPluginInfo? _removeTarget;
    private string _removeDisplayName = string.Empty;
    private bool _removeDeleteStorage;

    public LauncherPluginsViewModel(
        ILauncherOrchestrator orchestrator,
        IUiDispatcher dispatcher,
        Func<bool>? canInteract = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        ArgumentNullException.ThrowIfNull(dispatcher);
        _canInteract = canInteract ?? (() => true);

        // Not gated on the window's CanInteract: that is false whenever a modal is open, and this
        // dialog is one, so it would disable its own Install button. Its Confirm is gated on its own
        // IsOpen and IsBusy, like the remove dialog's.
        InstallDialog = new PluginInstallDialogViewModel();
        CheckNowCommand = new AsyncRelayCommand(
            CheckNowAsync,
            () => _canInteract() && !IsBusy && _composition is not null);
        AddFromUrlCommand = new AsyncRelayCommand(
            AddFromUrlAsync,
            () => _canInteract() && !IsBusy && _composition is not null
                && !string.IsNullOrWhiteSpace(AddFromUrlText));
        ConfirmRemoveCommand = new RelayCommand(
            ConfirmRemove,
            () => IsRemoveDialogOpen && !IsBusy);
        CancelRemoveCommand = new RelayCommand(
            () => IsRemoveDialogOpen = false,
            () => !IsBusy);
    }

    public ObservableCollection<PluginDiscoverRowViewModel> Discover { get; } = [];

    public ObservableCollection<PluginInstalledRowViewModel> Installed { get; } = [];

    public bool HasDiscover => Discover.Count > 0;

    public bool HasInstalled => Installed.Count > 0;

    private readonly List<PluginInstalledRowViewModel> _allInstalled = [];
    private readonly List<PluginDiscoverRowViewModel> _allDiscover = [];
    private string _installedFilter = "";
    private string _discoverFilter = "";

    public string InstalledFilter
    {
        get => _installedFilter;
        set { if (SetProperty(ref _installedFilter, value ?? "")) ApplyFilters(); }
    }

    public string DiscoverFilter
    {
        get => _discoverFilter;
        set { if (SetProperty(ref _discoverFilter, value ?? "")) ApplyFilters(); }
    }

    public string InstalledEmptyText => _installedFilter.Length > 0
        ? "No installed plugin matches that search."
        : "No plugins are installed yet.";

    public string DiscoverEmptyText => _discoverFilter.Length > 0
        ? "No listed plugin matches that search."
        : "No listed plugins are available to install.";

    private static bool Matches(string filter, params string?[] fields) =>
        filter.Length == 0
        || fields.Any(field => field is not null && field.Contains(filter, StringComparison.OrdinalIgnoreCase));

    /// <summary>Rebuilds the two shown lists from the full ones, keeping only what the searches match.</summary>
    private void ApplyFilters()
    {
        string installedFilter = _installedFilter.Trim();
        Installed.Clear();
        foreach (PluginInstalledRowViewModel row in _allInstalled
            .Where(row => Matches(installedFilter, row.DisplayName, row.Id, row.SourceBadge)))
        {
            Installed.Add(row);
        }

        string discoverFilter = _discoverFilter.Trim();
        Discover.Clear();
        foreach (PluginDiscoverRowViewModel row in _allDiscover
            .Where(row => Matches(discoverFilter, row.Name, row.Id, row.Author, row.Description, row.Repo)))
        {
            Discover.Add(row);
        }

        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(HasDiscover));
        OnPropertyChanged(nameof(InstalledEmptyText));
        OnPropertyChanged(nameof(DiscoverEmptyText));
    }

    /// <summary>
    /// Wide panels put Discover beside Installed; narrow ones stack them, installed first, so the
    /// plugins someone already has stay at the top of the scroll.
    /// </summary>
    public const double SideBySideWidth = 820;

    private bool _isSideBySide;

    public bool IsSideBySide
    {
        get => _isSideBySide;
        set
        {
            if (!SetProperty(ref _isSideBySide, value)) return;
            OnPropertyChanged(nameof(InstalledColumn));
            OnPropertyChanged(nameof(InstalledRow));
            OnPropertyChanged(nameof(InstalledColumnSpan));
            OnPropertyChanged(nameof(InstalledRowSpan));
            OnPropertyChanged(nameof(DiscoverColumn));
            OnPropertyChanged(nameof(DiscoverRow));
            OnPropertyChanged(nameof(DiscoverColumnSpan));
            OnPropertyChanged(nameof(DiscoverRowSpan));
        }
    }

    public void SetPanelWidth(double width) => IsSideBySide = width >= SideBySideWidth;

    public int InstalledColumn => IsSideBySide ? 1 : 0;
    public int InstalledRow => 0;
    public int InstalledColumnSpan => IsSideBySide ? 1 : 2;
    public int InstalledRowSpan => IsSideBySide ? 2 : 1;
    public int DiscoverColumn => 0;
    public int DiscoverRow => IsSideBySide ? 0 : 1;
    public int DiscoverColumnSpan => IsSideBySide ? 1 : 2;
    public int DiscoverRowSpan => IsSideBySide ? 2 : 1;

    public PluginInstallDialogViewModel InstallDialog { get; }

    public AsyncRelayCommand CheckNowCommand { get; }

    public AsyncRelayCommand AddFromUrlCommand { get; }

    public RelayCommand ConfirmRemoveCommand { get; }

    public RelayCommand CancelRemoveCommand { get; }

    public string AddFromUrlText
    {
        get => _addFromUrlText;
        set
        {
            if (SetProperty(ref _addFromUrlText, value))
            {
                AddFromUrlCommand.NotifyCanExecuteChanged();
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

    /// <summary>An informational result, e.g. "already installed" from Add from URL: shown in the
    /// panel's normal text, not the error style, since nothing went wrong.</summary>
    public string? StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatusText));
            }
        }
    }

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsRateLimited
    {
        get => _isRateLimited;
        private set => SetProperty(ref _isRateLimited, value);
    }

    /// <summary>Launcher-wide (L-319 amendment): offers a beta-only repo in Discover and Add from
    /// URL when on, persisted through the orchestrator like every other launcher setting. Off keeps
    /// every resolve on <see cref="PluginReleaseChannel.Stable"/>, exactly as before the setting
    /// existed. Toggling re-runs Discover's own details for the rows already showing, never a full
    /// Check.</summary>
    public bool ShowBetaPlugins
    {
        get => _showBetaPlugins;
        set
        {
            if (!SetProperty(ref _showBetaPlugins, value))
            {
                return;
            }

            _orchestrator.SetShowBetaPlugins(value);
            foreach (PluginDiscoverRowViewModel row in _allDiscover)
            {
                _discoverDetailsCache.Remove(row.Id);
                row.LatestVersion = null;
                row.Compatibility = null;
            }

            _ = RefreshDiscoverDetailsAsync();
        }
    }

    /// <summary>What Discover's own details and Add from URL resolve with (L-319 amendment).</summary>
    private PluginReleaseChannel DiscoverChannel =>
        ShowBetaPlugins ? PluginReleaseChannel.Beta : PluginReleaseChannel.Stable;

    public bool IsUsingCachedList => _listAgeUtc is not null;

    public string ListAgeText => _listAgeUtc is { } age
        ? $"Showing the plugin list from {FormatAge(DateTimeOffset.UtcNow - age)} ago."
        : string.Empty;

    public bool IsRemoveDialogOpen
    {
        get => _isRemoveDialogOpen;
        private set
        {
            if (SetProperty(ref _isRemoveDialogOpen, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public string RemoveDisplayName
    {
        get => _removeDisplayName;
        private set => SetProperty(ref _removeDisplayName, value);
    }

    public bool RemoveDeleteStorage
    {
        get => _removeDeleteStorage;
        set => SetProperty(ref _removeDeleteStorage, value);
    }

    /// <summary>Wires the real backend (App.axaml.cs); left unset, the panel shows no rows and its
    /// commands stay disabled, matching the other child view models' unavailable defaults.</summary>
    internal void Configure(
        LauncherPluginComposition composition,
        Func<ClientVersionResolution?> clientVersionResolver)
    {
        _composition = composition ?? throw new ArgumentNullException(nameof(composition));
        _clientVersionResolver = clientVersionResolver
            ?? throw new ArgumentNullException(nameof(clientVersionResolver));
        SetProperty(
            ref _showBetaPlugins,
            _orchestrator.GetSnapshot().ShowBetaPlugins,
            nameof(ShowBetaPlugins));
        NotifyCommandStates();
    }

    private async Task CheckNowAsync()
    {
        if (_composition is null || IsBusy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        IsBusy = true;
        Error = null;
        // Clears whatever Add from URL left on this line; install and remove both trigger a check.
        StatusText = null;
        IsRateLimited = false;
        try
        {
            PluginCheckOutcome outcome = await _composition
                .CheckAsync(_clientVersionResolver(), cancellation.Token)
                .ConfigureAwait(true);
            ApplyOutcome(outcome);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "The plugin list could not be checked."
                : ex.Message;
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

    private void ApplyOutcome(PluginCheckOutcome outcome)
    {
        // The catalog the next launched session filters blocked ids against (L-302), regardless
        // of whether this pass fetched fresh or fell back to the cache.
        _orchestrator.SetPluginCatalog(outcome.Catalog);

        IsRateLimited = outcome.RateLimited;
        _listAgeUtc = outcome.ListAgeUtc;
        OnPropertyChanged(nameof(IsUsingCachedList));
        OnPropertyChanged(nameof(ListAgeText));
        if (IsRateLimited)
        {
            Error = "GitHub is rate limiting; try later.";
        }
        else if (outcome.Catalog is null)
        {
            Error = "Could not reach the plugin list.";
        }

        _allInstalled.Clear();
        foreach (InstalledPluginInfo info in outcome.Installed)
        {
            outcome.UpdatesAvailable.TryGetValue(info.Id, out PluginUpdateAvailability? availability);
            outcome.UpdateWithheldReasons.TryGetValue(info.Id, out string? withheldReason);
            _allInstalled.Add(BuildInstalledRow(info, availability, withheldReason));
        }

        _allDiscover.Clear();
        foreach (PluginDiscoverEntry entry in outcome.Discover)
        {
            var install = new RelayCommand(
                () => OpenDiscoverInstallDialog(entry),
                () => _canInteract() && !IsBusy);
            var row = new PluginDiscoverRowViewModel(
                entry.Id, entry.Name, entry.Author, entry.Description, entry.Repo, install);
            if (_discoverDetailsCache.TryGetValue(entry.Id, out DiscoverDetails cached))
            {
                row.LatestVersion = cached.LatestVersion;
                row.Compatibility = cached.Compatibility;
                row.CompatibilityIsWarning = cached.CompatibilityIsWarning;
                row.Capabilities = cached.Capabilities;
            }

            _allDiscover.Add(row);
        }

        ApplyFilters();
    }

    /// <summary>Builds one Installed row from the inventory and its update check, reused by both a
    /// full Check pass and a single-plugin re-check after the beta toggle (L-319).</summary>
    private PluginInstalledRowViewModel BuildInstalledRow(
        InstalledPluginInfo info, PluginUpdateAvailability? availability, string? withheldReason)
    {
        bool canRemove = info.Source is InstalledPluginSource.Managed or InstalledPluginSource.Direct;
        bool updateAvailable = info.Source == InstalledPluginSource.Managed && availability is not null;
        bool showBetaToggle = info.Source == InstalledPluginSource.Managed;
        bool isBetaChannel = showBetaToggle
            && _composition!.RecordStore.Find(info.Id)?.Channel == PluginReleaseChannel.Beta;
        bool isPrerelease = LauncherVersion.TryParse(info.Version, out LauncherVersion? installedVersion)
            && installedVersion.IsPreRelease;
        RelayCommand? updateCommand = updateAvailable
            ? new RelayCommand(
                () => OpenUpdateDialog(info, availability!.Tag, availability!.Version, availability!.Capabilities),
                () => _canInteract() && !IsBusy)
            : null;
        RelayCommand? removeCommand = canRemove
            ? new RelayCommand(() => OpenRemoveDialog(info), () => _canInteract() && !IsBusy)
            : null;

        // The toggle callback needs the row it belongs to (to revert it on a lease refusal); the
        // row doesn't exist until the constructor returns, so the closure reads it back through
        // this local once construction has finished.
        PluginInstalledRowViewModel? self = null;
        var row = new PluginInstalledRowViewModel(
            info.Id,
            info.DisplayName,
            info.Version,
            DescribeSource(info.Source, info.ListedSource),
            info.Compatibility,
            info.CompatibilityIsWarning,
            info.Blocked,
            info.Conflict,
            canRemove,
            showBetaToggle,
            isBetaChannel,
            isPrerelease,
            updateAvailable,
            updateAvailable ? availability!.Version : null,
            updateAvailable ? availability!.CompatibilityNote : null,
            updateAvailable && availability!.CompatibilityIsWarning,
            withheldReason,
            updateCommand,
            removeCommand,
            info.Refusal,
            info.HasDuplicate,
            onBetaToggled: isBeta => _ = ToggleBetaAsync(self!, isBeta),
            canToggleBeta: () => _canInteract() && !IsBusy,
            ReadInstalledCapabilities(info));
        self = row;
        return row;
    }

    /// <summary>The beta toggle's own write (L-319): sets the channel under the installer's
    /// exclusive lease, then re-checks that one plugin (never the full pass every other trigger
    /// runs) and rebuilds its row with the fresh result. A lease refusal reverts the toggle and is
    /// shown the way Remove shows it.</summary>
    private async Task ToggleBetaAsync(PluginInstalledRowViewModel row, bool isBeta)
    {
        if (_composition is null || IsBusy)
        {
            row.RevertBetaChannel(!isBeta);
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            _composition.Installer.SetChannel(
                row.Id, isBeta ? PluginReleaseChannel.Beta : PluginReleaseChannel.Stable);
            PluginSingleCheckResult result = await _composition
                .CheckSingleAsync(row.Id, _clientVersionResolver())
                .ConfigureAwait(true);
            ReplaceInstalledRow(row.Id, result.Available, result.WithheldReason);
        }
        catch (LauncherUpdateException ex)
        {
            row.RevertBetaChannel(!isBeta);
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "The plugin's channel could not be changed."
                : ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Rebuilds one Installed row in place, keeping its position in both the full list and
    /// whatever the current search filter shows.</summary>
    private void ReplaceInstalledRow(
        string id, PluginUpdateAvailability? availability, string? withheldReason)
    {
        if (_composition is null)
        {
            return;
        }

        InstalledPluginInfo? info = _composition.Inventory.Find(
            id, _clientVersionResolver(), _composition.CurrentCatalog);
        if (info is null)
        {
            return;
        }

        PluginInstalledRowViewModel updated = BuildInstalledRow(info, availability, withheldReason);
        int allIndex = _allInstalled.FindIndex(
            row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase));
        if (allIndex >= 0)
        {
            _allInstalled[allIndex] = updated;
        }

        for (int index = 0; index < Installed.Count; index++)
        {
            if (string.Equals(Installed[index].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                Installed[index] = updated;
                break;
            }
        }
    }

    /// <summary>Opening Discover's own request (plan, "Request budget"): one <c>plugin.json</c> per
    /// listed, not-installed plugin still missing its details, cached here for the rest of the
    /// launcher session so switching tabs or checking again never re-fetches it.</summary>
    internal async Task RefreshDiscoverDetailsAsync()
    {
        if (_composition is null)
        {
            return;
        }

        foreach (PluginDiscoverRowViewModel row in Discover.ToArray())
        {
            if (_discoverDetailsCache.ContainsKey(row.Id))
            {
                continue;
            }

            PluginReleaseResolveResult result;
            try
            {
                result = await _composition.ReleaseResolver
                    .ResolveAsync(row.Repo, DiscoverChannel)
                    .ConfigureAwait(true);
            }
            catch (LauncherUpdateException)
            {
                continue;
            }

            // Rate-limited, unavailable, an invalid manifest and a prerelease latest are all
            // skipped the same way (L-319): the row just keeps showing no details this pass.
            if (result.Status != PluginReleaseResolveStatus.Success)
            {
                continue;
            }

            PluginReleaseResolution resolution = result.Resolution!;
            LauncherPluginManifest manifest = resolution.Manifest;

            LauncherVersion? remoteVersion = LauncherVersion.TryParse(manifest.Version, out LauncherVersion? parsed)
                ? parsed
                : null;
            // A version-specific block (L-314) can only be judged once the latest version is known;
            // a wildcard block never reaches here, since the Check pipeline already hid the row.
            if (_composition.CurrentCatalog?.IsBlocked(row.Id, remoteVersion) == true)
            {
                Discover.Remove(row);
                OnPropertyChanged(nameof(HasDiscover));
                continue;
            }

            LauncherVersion? clientVersion = _clientVersionResolver()?.Version;
            LauncherPluginCompatibility.CompatibilityDescription compatibility =
                LauncherPluginCompatibility.Describe(manifest, clientVersion);
            var details = new DiscoverDetails(
                manifest.Version, resolution.Tag, compatibility.Text, compatibility.IsWarning,
                manifest.Capabilities);
            _discoverDetailsCache[row.Id] = details;
            row.LatestVersion = details.LatestVersion;
            row.Compatibility = details.Compatibility;
            row.CompatibilityIsWarning = details.CompatibilityIsWarning;
            row.Capabilities = details.Capabilities;
        }
    }

    private readonly record struct DiscoverDetails(
        string LatestVersion,
        string Tag,
        string Compatibility,
        bool CompatibilityIsWarning,
        IReadOnlyList<LauncherPluginCapabilityDeclaration> Capabilities);

    /// <summary>Discover's Install button: the cache may not hold this plugin's details yet (its
    /// own request, "Request budget"), so both the tag and version are read live, not captured when
    /// the row was built.</summary>
    private void OpenDiscoverInstallDialog(PluginDiscoverEntry entry)
    {
        _discoverDetailsCache.TryGetValue(entry.Id, out DiscoverDetails cached);
        OpenInstallDialog(
            entry.Repo, entry.Id, entry.Name, isUpdate: false, cached.Tag, cached.LatestVersion,
            cached.Capabilities);
    }

    /// <summary>Opens the install/update dialog for a repo. <paramref name="pinnedTag"/> is the
    /// release tag whichever caller already resolved (the update check, Discover's own details, or
    /// Add from URL); when Discover hasn't fetched details yet, it is null and Confirm resolves
    /// latest itself instead of the install ever doing so (L-319). <paramref name="offeredVersion"/>
    /// travels the same way, so the dialog's notice can name a pre-release offer (L-319).</summary>
    private void OpenInstallDialog(
        string repo,
        string pluginId,
        string displayName,
        bool isUpdate,
        string? pinnedTag,
        string? offeredVersion,
        IReadOnlyList<LauncherPluginCapabilityDeclaration> capabilities)
    {
        if (_composition is null)
        {
            return;
        }

        bool isListed = _composition.CurrentCatalog?.Plugins.Any(entry =>
            string.Equals(entry.Repo, repo, StringComparison.Ordinal)) == true;
        InstallDialog.Open(
            repo,
            pluginId,
            displayName,
            isListed,
            isUpdate,
            offeredVersion,
            BuildCharacterOptions(),
            cancellationToken => InstallAsync(repo, pinnedTag, cancellationToken),
            EnableForCharacters,
            capabilities);
    }

    private void OpenUpdateDialog(
        InstalledPluginInfo info,
        string tag,
        string version,
        IReadOnlyList<LauncherPluginCapabilityDeclaration> capabilities)
    {
        if (info.Repo is { } repo)
        {
            OpenInstallDialog(repo, info.Id, info.DisplayName, isUpdate: true, tag, version, capabilities);
        }
    }

    private async Task<PluginInstallResult> InstallAsync(
        string repo, string? pinnedTag, CancellationToken cancellationToken)
    {
        string tag = pinnedTag ?? await ResolveLatestTagAsync(repo, cancellationToken).ConfigureAwait(true);
        PluginInstallResult result = await _composition!.Installer.InstallOrUpdateAsync(
                repo,
                tag,
                _composition.CurrentCatalog,
                _clientVersionResolver(),
                cancellationToken)
            .ConfigureAwait(true);
        _ = CheckNowAsync();
        return result;
    }

    /// <summary>Discover's own fallback when its details fetch hasn't populated a tag yet: resolved
    /// once, right before install, never inside <see cref="PluginInstaller.InstallOrUpdateAsync"/>
    /// itself.</summary>
    private async Task<string> ResolveLatestTagAsync(string repo, CancellationToken cancellationToken)
    {
        PluginReleaseResolveResult result = await _composition!.ReleaseResolver
            .ResolveAsync(repo, PluginReleaseChannel.Stable, cancellationToken)
            .ConfigureAwait(true);
        return result.Status switch
        {
            PluginReleaseResolveStatus.Success => result.Resolution!.Tag,
            PluginReleaseResolveStatus.RateLimited =>
                throw new LauncherUpdateException("GitHub is rate limiting; try later."),
            PluginReleaseResolveStatus.Prerelease =>
                throw new LauncherUpdateException(result.Error!),
            PluginReleaseResolveStatus.Invalid =>
                throw new LauncherUpdateException("That repository's plugin.json could not be read."),
            _ => throw new LauncherUpdateException("The plugin release is unavailable."),
        };
    }

    /// <summary>The install dialog's only profile write, and only for the characters chosen there
    /// (L-300). Reuses the same <see cref="ILauncherOrchestrator.UpdateCharacterSettings"/> path the
    /// character options dialog saves through, so every write to a character's plugin list goes
    /// through the orchestrator's own lock. Runs after the dialog has already closed (install
    /// succeeded), so any trouble here is reported on the panel, not the dialog.</summary>
    internal void EnableForCharacters(string pluginId, IReadOnlyList<PluginCharacterOption> characters)
    {
        IReadOnlyList<LauncherPluginHostKind> hosts = ReadInstalledHosts(pluginId);
        List<LauncherCharacterSnapshot> snapshots = [.. _orchestrator.GetSnapshot().Servers
            .SelectMany(server => server.Accounts)
            .SelectMany(account => account.Characters)];
        var skipped = new List<string>();
        try
        {
            foreach (PluginCharacterOption character in characters)
            {
                LauncherCharacterSnapshot? snapshot = snapshots.FirstOrDefault(candidate =>
                    string.Equals(candidate.ServerName, character.ServerName, StringComparison.Ordinal)
                    && string.Equals(candidate.AccountName, character.AccountName, StringComparison.Ordinal)
                    && string.Equals(candidate.Name, character.CharacterName, StringComparison.Ordinal));
                if (snapshot is null)
                {
                    continue;
                }

                LauncherPluginHostKind characterHost = snapshot.LaunchMode == LaunchMode.Headless
                    ? LauncherPluginHostKind.Headless
                    : LauncherPluginHostKind.Graphical;
                if (!hosts.Contains(characterHost))
                {
                    skipped.Add(character.DisplayName);
                    continue;
                }

                List<string> plugins = snapshot.Plugins
                    .Where(id => !string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (!plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
                {
                    plugins.Add(pluginId);
                }

                _orchestrator.UpdateCharacterSettings(
                    character.ServerName,
                    character.AccountName,
                    character.CharacterName,
                    snapshot.LaunchMode,
                    plugins,
                    snapshot.LoginCommands);
            }
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "The plugin installed, but could not be enabled for every chosen character."
                : ex.Message;
            return;
        }

        if (skipped.Count > 0)
        {
            Error = $"Not enabled for {string.Join(", ", skipped)}: "
                + "this plugin does not support that launch mode.";
        }
    }

    private string InstalledDisplayName(string pluginId) =>
        _composition?.Inventory.Find(pluginId, _clientVersionResolver(), _composition.CurrentCatalog)
            ?.DisplayName
        ?? pluginId;

    private IReadOnlyList<LauncherPluginHostKind> ReadInstalledHosts(string pluginId)
    {
        InstalledPluginInfo? info = _composition?.Inventory.Find(
            pluginId, _clientVersionResolver(), _composition.CurrentCatalog);
        if (info is null)
        {
            return [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless];
        }

        try
        {
            return LauncherPluginManifest.Parse(
                File.ReadAllText(Path.Combine(info.Directory, "plugin.json"))).Hosts
                ?? [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless];
        }
        catch (Exception ex) when (ex is IOException or LauncherPluginManifestException)
        {
            return [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless];
        }
    }

    /// <summary>The installed row's own count, read straight from its <c>plugin.json</c> the way
    /// <see cref="ReadInstalledHosts"/> does, since <see cref="InstalledPluginInfo"/> does not carry
    /// it.</summary>
    private static IReadOnlyList<LauncherPluginCapabilityDeclaration> ReadInstalledCapabilities(
        InstalledPluginInfo info)
    {
        try
        {
            return LauncherPluginManifest.Parse(
                File.ReadAllText(Path.Combine(info.Directory, "plugin.json"))).Capabilities;
        }
        catch (Exception ex) when (ex is IOException or LauncherPluginManifestException)
        {
            return [];
        }
    }

    private IReadOnlyList<PluginCharacterOption> BuildCharacterOptions() =>
        [.. _orchestrator.GetSnapshot().Servers
            .SelectMany(server => server.Accounts)
            .SelectMany(account => account.Characters)
            .Select(character => new PluginCharacterOption(
                character.ServerName,
                character.AccountName,
                character.Name,
                $"{character.Name} ({character.AccountName}@{character.ServerName})"))];

    private async Task AddFromUrlAsync()
    {
        if (_composition is null || IsBusy)
        {
            return;
        }

        if (!GitHubReleaseLocator.TryParseRepoUrl(AddFromUrlText, out string repo))
        {
            Error = "Enter a URL like https://github.com/owner/name.";
            return;
        }

        InstalledPluginRecord? installedByRepo = _composition.RecordStore.Records.FirstOrDefault(
            record => string.Equals(record.Repo, repo, StringComparison.OrdinalIgnoreCase));
        if (installedByRepo is not null)
        {
            AddFromUrlText = string.Empty;
            StatusText = $"{InstalledDisplayName(installedByRepo.Id)} is already installed.";
            return;
        }

        IsBusy = true;
        Error = null;
        StatusText = null;
        try
        {
            PluginReleaseResolveResult result = await _composition.ReleaseResolver
                .ResolveAsync(repo, DiscoverChannel)
                .ConfigureAwait(true);
            switch (result.Status)
            {
                case PluginReleaseResolveStatus.Success:
                    PluginReleaseResolution resolution = result.Resolution!;
                    LauncherPluginManifest manifest = resolution.Manifest;
                    AddFromUrlText = string.Empty;
                    // Same repo already installed (a race with another Add or Check between the
                    // resolve above and here): same neutral status as the early check above.
                    InstalledPluginRecord? installedById = _composition.RecordStore.Find(manifest.Id);
                    if (installedById is not null
                        && string.Equals(installedById.Repo, repo, StringComparison.OrdinalIgnoreCase))
                    {
                        StatusText = $"{manifest.DisplayName} is already installed.";
                        break;
                    }

                    // The id is already installed from a different repo: refuse up front with the
                    // same reason PluginInstaller would give, rather than opening a dialog whose
                    // Install could only fail.
                    if (installedById is not null)
                    {
                        Error = $"'{manifest.Id}' is already installed from '{installedById.Repo}'.";
                        break;
                    }

                    // Blocked for "*" or for this fetched version (L-314): refuse the same way,
                    // rather than opening a dialog whose Install could only fail.
                    LauncherVersion? manifestVersion = LauncherVersion.TryParse(
                        manifest.Version, out LauncherVersion? parsedVersion)
                        ? parsedVersion
                        : null;
                    if (_composition.CurrentCatalog?.BlockReason(manifest.Id, manifestVersion)
                        is { } blockReason)
                    {
                        Error = $"'{manifest.Id}' is blocked: {blockReason}";
                        break;
                    }

                    OpenInstallDialog(
                        repo, manifest.Id, manifest.DisplayName, isUpdate: false,
                        resolution.Tag, manifest.Version, manifest.Capabilities);
                    break;
                case PluginReleaseResolveStatus.RateLimited:
                    Error = "GitHub is rate limiting; try later.";
                    break;
                case PluginReleaseResolveStatus.Invalid:
                    Error = "That repository's plugin.json could not be read.";
                    break;
                case PluginReleaseResolveStatus.Prerelease:
                    Error = result.Error!;
                    break;
                default:
                    Error = "No release was found for that repository.";
                    break;
            }
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message) ? "Could not add that plugin." : ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenRemoveDialog(InstalledPluginInfo info)
    {
        _removeTarget = info;
        RemoveDisplayName = info.DisplayName;
        RemoveDeleteStorage = false;
        Error = null;
        IsRemoveDialogOpen = true;
    }

    /// <summary>Escape's path to the remove dialog, matching <see cref="PluginInstallDialogViewModel.Close"/>:
    /// a no-op while the removal itself is running.</summary>
    public void CloseRemoveDialog()
    {
        if (IsBusy)
        {
            return;
        }

        IsRemoveDialogOpen = false;
    }

    private void ConfirmRemove()
    {
        if (_composition is null || _removeTarget is not { } target)
        {
            return;
        }

        try
        {
            if (target.Source == InstalledPluginSource.Direct)
            {
                _composition.Installer.RemoveDirect(target.Directory, RemoveDeleteStorage);
            }
            else
            {
                _composition.Installer.Remove(target.Id, RemoveDeleteStorage);
            }
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "The plugin could not be removed."
                : ex.Message;
            return;
        }

        IsRemoveDialogOpen = false;
        _ = CheckNowAsync();
        StripFromEveryCharacter(target.Id);
    }

    /// <summary>The remove dialog's own profile write (L-312): every character still holding the
    /// removed id loses it, so reinstalling it never inherits an old enable. Reuses the same
    /// <see cref="ILauncherOrchestrator.UpdateCharacterSettings"/> path <see cref="EnableForCharacters"/>
    /// saves through. Runs after <see cref="PluginInstaller.Remove"/> has already succeeded, so trouble
    /// here is reported without undoing the removal.</summary>
    private void StripFromEveryCharacter(string pluginId)
    {
        List<LauncherCharacterSnapshot> snapshots = [.. _orchestrator.GetSnapshot().Servers
            .SelectMany(server => server.Accounts)
            .SelectMany(account => account.Characters)];
        try
        {
            foreach (LauncherCharacterSnapshot snapshot in snapshots)
            {
                if (!snapshot.Plugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                List<string> plugins = snapshot.Plugins
                    .Where(id => !string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _orchestrator.UpdateCharacterSettings(
                    snapshot.ServerName,
                    snapshot.AccountName,
                    snapshot.Name,
                    snapshot.LaunchMode,
                    plugins,
                    snapshot.LoginCommands);
            }
        }
        catch (Exception ex)
        {
            Error = string.IsNullOrWhiteSpace(ex.Message)
                ? "The plugin was removed, but could not be unchecked for every character."
                : ex.Message;
        }
    }

    public void NotifyCommandStates()
    {
        CheckNowCommand.NotifyCanExecuteChanged();
        AddFromUrlCommand.NotifyCanExecuteChanged();
        ConfirmRemoveCommand.NotifyCanExecuteChanged();
        CancelRemoveCommand.NotifyCanExecuteChanged();
        foreach (PluginDiscoverRowViewModel row in Discover)
        {
            row.InstallCommand.NotifyCanExecuteChanged();
        }

        foreach (PluginInstalledRowViewModel row in Installed)
        {
            row.UpdateCommand?.NotifyCanExecuteChanged();
            row.RemoveCommand?.NotifyCanExecuteChanged();
            row.NotifyBetaToggleEnabledChanged();
        }
    }

    private static string DescribeSource(InstalledPluginSource source, PluginInstallSource? listedSource) =>
        source switch
        {
            InstalledPluginSource.Managed => listedSource == PluginInstallSource.Unlisted
                ? "Unlisted"
                : "Listed",
            InstalledPluginSource.Direct => "Direct install",
            InstalledPluginSource.Bundled => "Bundled",
            _ => source.ToString(),
        };

    private static string FormatAge(TimeSpan age) => age.TotalDays >= 1
        ? $"{age.TotalDays:0} day(s)"
        : age.TotalHours >= 1
            ? $"{age.TotalHours:0} hour(s)"
            : $"{Math.Max(1, age.TotalMinutes):0} minute(s)";
}
