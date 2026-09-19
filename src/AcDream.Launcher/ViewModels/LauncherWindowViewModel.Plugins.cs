using System.Collections.ObjectModel;
using AcDream.Launcher.Core.Orchestration;
using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.ViewModels;

/// <summary>One plugin offered in the character options checklist: an installed plugin compatible
/// with the character's launch mode, or an id already in the character's saved list that is no
/// longer offered there ("missing"), kept checked until the user unchecks it.</summary>
public sealed class CharacterPluginChoiceViewModel(
    string id,
    string displayName,
    bool isChecked,
    bool isMissing)
    : ObservableObject
{
    private bool _isChecked = isChecked;

    public string Id { get; } = id;

    public string DisplayName { get; } = displayName;

    public bool IsMissing { get; } = isMissing;

    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }
}

/// <summary>The two top-level panels the redesigned window switches between.</summary>
public enum LauncherMainTab
{
    Accounts,
    Plugins,
}

public sealed partial class LauncherWindowViewModel
{
    private PluginInventory? _pluginInventory;
    private LauncherMainTab _selectedTab = LauncherMainTab.Accounts;

    public LauncherPluginsViewModel Plugins { get; private set; } = null!;

    public ObservableCollection<CharacterPluginChoiceViewModel> CharacterPluginChoices { get; } = [];

    public LauncherMainTab SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsAccountsTabSelected));
                OnPropertyChanged(nameof(IsPluginsTabSelected));
            }
        }
    }

    public bool IsAccountsTabSelected => SelectedTab == LauncherMainTab.Accounts;

    public bool IsPluginsTabSelected => SelectedTab == LauncherMainTab.Plugins;

    public RelayCommand SelectAccountsTabCommand { get; private set; } = null!;

    public RelayCommand SelectPluginsTabCommand { get; private set; } = null!;

    private void InitializePlugins()
    {
        Plugins = new LauncherPluginsViewModel(_orchestrator, _dispatcher, () => CanInteract);
        Plugins.InstallDialog.PropertyChanged += OnModalPropertyChanged;
        Plugins.PropertyChanged += OnModalPropertyChanged;
        SelectAccountsTabCommand = new RelayCommand(() => SelectedTab = LauncherMainTab.Accounts);
        SelectPluginsTabCommand = new RelayCommand(() =>
        {
            SelectedTab = LauncherMainTab.Plugins;
            _ = Plugins.RefreshDiscoverDetailsAsync();
        });
    }

    /// <summary>Wires the real plugin backend, built by <c>LauncherPluginComposition</c> in
    /// App.axaml.cs; left unset (as in most tests), the Plugins tab shows no rows and the character
    /// checklist shows only ids already saved on the character, all as "missing".</summary>
    internal void ConfigurePlugins(
        LauncherPluginComposition composition,
        Func<ClientVersionResolution?> clientVersionResolver)
    {
        ArgumentNullException.ThrowIfNull(composition);
        _pluginInventory = composition.Inventory;
        Plugins.Configure(composition, clientVersionResolver);
    }

    /// <summary>Rebuilds the character options checklist against the live plugin inventory,
    /// filtered by the character's launch mode through <c>hosts</c>. Ids already saved on the
    /// character that are not offered here (not installed, or installed for the other host) are
    /// kept as checked, missing rows so a save never silently drops them.</summary>
    private void LoadCharacterPluginChoices(LauncherCharacterSnapshot? character)
    {
        CharacterPluginChoices.Clear();
        if (character is null)
        {
            return;
        }

        var configured = new HashSet<string>(character.Plugins, StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wrongHostDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var refusedDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var blockedDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        LauncherPluginHostKind host = character.LaunchMode == LaunchMode.Headless
            ? LauncherPluginHostKind.Headless
            : LauncherPluginHostKind.Graphical;

        if (_pluginInventory is not null)
        {
            IEnumerable<InstalledPluginInfo> installed = _pluginInventory
                .Build(clientResolution: null, Plugins.CurrentCatalog)
                .OrderBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase);
            foreach (InstalledPluginInfo info in installed)
            {
                LauncherPluginManifest? manifest = TryReadManifest(info.Directory);
                IReadOnlyList<LauncherPluginHostKind> hosts = manifest?.Hosts
                    ?? [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless];
                if (!hosts.Contains(host))
                {
                    wrongHostDisplayNames[info.Id] = info.DisplayName;
                    continue;
                }

                if (info.Refusal is not null || info.HasDuplicate)
                {
                    refusedDisplayNames[info.Id] = info.DisplayName;
                    continue;
                }

                // A blocked plugin is dropped at launch, so it is not offered as a working choice.
                if (info.Blocked is not null)
                {
                    blockedDisplayNames[info.Id] = info.DisplayName;
                    continue;
                }

                if (!placed.Add(info.Id))
                {
                    continue;
                }

                CharacterPluginChoices.Add(new CharacterPluginChoiceViewModel(
                    info.Id,
                    info.DisplayName,
                    isChecked: configured.Contains(info.Id),
                    isMissing: false));
            }
        }

        foreach (string id in character.Plugins)
        {
            if (string.Equals(id, "none", StringComparison.OrdinalIgnoreCase) || !placed.Add(id))
            {
                continue;
            }

            string displayName = wrongHostDisplayNames.TryGetValue(id, out string? installedName)
                ? $"{installedName} (not available for this mode)"
                : refusedDisplayNames.TryGetValue(id, out string? refusedName)
                    ? $"{refusedName} (refused)"
                    : blockedDisplayNames.TryGetValue(id, out string? blockedName)
                        ? $"{blockedName} (blocked)"
                        : $"{id} (missing)";
            CharacterPluginChoices.Add(new CharacterPluginChoiceViewModel(
                id, displayName, isChecked: true, isMissing: true));
        }
    }

    private (string Server, string Account)? _rowOptionsAccount;
    private readonly Dictionary<string, bool> _accountChoiceInitial =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<LauncherPluginHostKind>> _accountChoiceHosts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while the row options dialog is open for a row that launches to the
    /// character screen, where a plugin choice covers every character on the account.</summary>
    public bool IsAccountRowOptions => _rowOptionsAccount is not null;

    public bool ShowRowLogonCommands => !IsAccountRowOptions;

    public string RowOptionsTitle => _rowOptionsAccount is { } account
        ? $"{account.Account} · all characters"
        : SelectionTitle;

    public string RowOptionsPluginsCaption => IsAccountRowOptions
        ? "Plugins · a checked plugin is enabled for every character on this account"
        : "Plugins · only checked plugins load";

    private void SetRowOptionsAccount((string Server, string Account)? account)
    {
        _rowOptionsAccount = account;
        OnPropertyChanged(nameof(IsAccountRowOptions));
        OnPropertyChanged(nameof(ShowRowLogonCommands));
        OnPropertyChanged(nameof(RowOptionsTitle));
        OnPropertyChanged(nameof(RowOptionsPluginsCaption));
    }

    private LauncherAccountSnapshot? FindAccountSnapshot((string Server, string Account) key) =>
        _orchestrator.GetSnapshot().Servers
            .FirstOrDefault(server => string.Equals(server.Name, key.Server, StringComparison.Ordinal))
            ?.Accounts.FirstOrDefault(account =>
                string.Equals(account.AccountName, key.Account, StringComparison.Ordinal));

    /// <summary>The checklist for a row that launches to the character screen. The launcher cannot
    /// know which character will be picked there, so such a launch loads only what every character
    /// on the account has enabled; this list edits exactly that, one plugin for all characters.</summary>
    private void LoadAccountPluginChoices(LauncherAccountSnapshot account)
    {
        CharacterPluginChoices.Clear();
        _accountChoiceInitial.Clear();
        _accountChoiceHosts.Clear();
        if (_pluginInventory is null)
        {
            return;
        }

        int total = account.Characters.Count;
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<InstalledPluginInfo> installed = _pluginInventory
            .Build(clientResolution: null, Plugins.CurrentCatalog)
            .OrderBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase);
        foreach (InstalledPluginInfo info in installed)
        {
            if (info.Refusal is not null || info.HasDuplicate || info.Blocked is not null
                || !placed.Add(info.Id))
            {
                continue;
            }

            int enabled = account.Characters.Count(character =>
                character.Plugins.Contains(info.Id, StringComparer.OrdinalIgnoreCase));
            bool all = total > 0 && enabled == total;
            string displayName = enabled > 0 && !all
                ? $"{info.DisplayName} (on {enabled} of {total} characters)"
                : info.DisplayName;
            _accountChoiceInitial[info.Id] = all;
            _accountChoiceHosts[info.Id] = TryReadManifest(info.Directory)?.Hosts
                ?? [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless];
            CharacterPluginChoices.Add(new CharacterPluginChoiceViewModel(
                info.Id, displayName, isChecked: all, isMissing: false));
        }
    }

    /// <summary>Writes only the choices the player changed, so a plugin left enabled for some
    /// characters stays that way unless its box was touched.</summary>
    private void SaveAccountPluginChoices((string Server, string Account) key)
    {
        LauncherAccountSnapshot account = FindAccountSnapshot(key)
            ?? throw new LauncherOperationException("That account no longer exists.");
        var skipped = new List<string>();
        try
        {
            foreach (LauncherCharacterSnapshot character in account.Characters)
            {
                List<string> plugins = character.Plugins
                    .Where(id => !string.Equals(id, "none", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                bool changed = false;
                LauncherPluginHostKind host = character.LaunchMode == LaunchMode.Headless
                    ? LauncherPluginHostKind.Headless
                    : LauncherPluginHostKind.Graphical;
                foreach (CharacterPluginChoiceViewModel choice in CharacterPluginChoices)
                {
                    if (!_accountChoiceInitial.TryGetValue(choice.Id, out bool initial)
                        || initial == choice.IsChecked)
                    {
                        continue;
                    }

                    bool has = plugins.Contains(choice.Id, StringComparer.OrdinalIgnoreCase);
                    if (choice.IsChecked && !has)
                    {
                        if (!_accountChoiceHosts[choice.Id].Contains(host))
                        {
                            skipped.Add($"{choice.DisplayName} for {character.Name}");
                            continue;
                        }

                        plugins.Add(choice.Id);
                        changed = true;
                    }
                    else if (!choice.IsChecked && has)
                    {
                        plugins.RemoveAll(id =>
                            string.Equals(id, choice.Id, StringComparison.OrdinalIgnoreCase));
                        changed = true;
                    }
                }

                if (changed)
                {
                    _orchestrator.UpdateCharacterSettings(
                        character.ServerName,
                        character.AccountName,
                        character.Name,
                        character.LaunchMode,
                        plugins,
                        character.LoginCommands);
                }
            }

            LastError = skipped.Count > 0
                ? $"Not enabled: {string.Join(", ", skipped)}. That plugin does not support the character's launch mode."
                : null;
            OperationStatus = $"Saved plugins for every character on {key.Account}.";
            RefreshFromCore(new SelectionKey(
                LauncherTreeNodeKind.Account, key.Server, key.Account, null));
            if (FindAccountSnapshot(key) is { } saved) LoadAccountPluginChoices(saved);
        }
        catch (Exception ex)
        {
            LastError = SafeDisplayError(ex, secret: null);
        }
    }

    /// <summary>Blank means none: unchecking every plugin saves an empty list, not the old
    /// literal "none" sentinel a free-text box once needed.</summary>
    private IReadOnlyList<string> CheckedCharacterPluginIds() =>
        [.. CharacterPluginChoices.Where(choice => choice.IsChecked).Select(choice => choice.Id)];

    private static LauncherPluginManifest? TryReadManifest(string directory)
    {
        try
        {
            return LauncherPluginManifest.Parse(
                File.ReadAllText(Path.Combine(directory, "plugin.json")));
        }
        catch (Exception ex) when (ex is IOException or LauncherPluginManifestException)
        {
            return null;
        }
    }
}
