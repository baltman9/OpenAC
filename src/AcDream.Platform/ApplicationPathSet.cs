namespace AcDream.Platform;

public interface IApplicationPathEnvironment
{
    bool IsWindows { get; }

    bool IsMacOS => false;

    string CurrentDirectory { get; }

    string? GetEnvironmentVariable(string name);

    string GetFolderPath(Environment.SpecialFolder folder);

    /// <summary>
    /// The text of a small file, or null when it does not exist. The install
    /// folder pointer is read through this so tests can supply one without
    /// touching the real user profile.
    /// </summary>
    string? ReadTextFile(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;
}

internal sealed class ApplicationPathEnvironment
    : IApplicationPathEnvironment
{
    internal static ApplicationPathEnvironment Instance { get; } = new();

    private ApplicationPathEnvironment()
    {
    }

    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsMacOS => OperatingSystem.IsMacOS();

    public string CurrentDirectory => Environment.CurrentDirectory;

    public string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name);

    public string GetFolderPath(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder);
}

/// <summary>How the install root of a resolved path set was chosen.</summary>
public enum ApplicationRootSource
{
    /// <summary>A caller argument named the root or one of its members.</summary>
    Explicit,

    /// <summary>An environment variable named the root or one of its members.</summary>
    Environment,

    /// <summary>The pointer file in the default root named the root.</summary>
    Pointer,

    /// <summary>Nothing named a root; the per-user default is used.</summary>
    Default,
}

/// <summary>
/// Every folder the client, the windowless host and the launcher read or
/// write, derived from one install root. The root holds <c>app/</c>,
/// <c>data/</c>, <c>settings/</c>, <c>plugins/</c>, <c>vtank/</c>,
/// <c>logs/</c> and <c>cache/</c>. The three members stay separately
/// overridable so automation can isolate settings, data or cache on its own;
/// the data member is the root itself.
/// </summary>
public sealed record ApplicationPathSet(
    string ConfigDirectory,
    string DataDirectory,
    string CacheDirectory)
{
    /// <summary>Names the install root for this process and its children.</summary>
    public const string RootEnvironmentVariable = "ACDREAM_ROOT_DIR";

    /// <summary>Overrides the settings folder alone.</summary>
    public const string ConfigEnvironmentVariable = "ACDREAM_CONFIG_DIR";

    /// <summary>Overrides the data folder (the root itself) alone.</summary>
    public const string DataEnvironmentVariable = "ACDREAM_DATA_DIR";

    /// <summary>Overrides the cache folder alone.</summary>
    public const string CacheEnvironmentVariable = "ACDREAM_CACHE_DIR";

    /// <summary>The name of the per-plugin folder that holds its private files.</summary>
    public const string PluginFilesFolderName = "files";

    /// <summary>How <see cref="RootDirectory"/> was chosen.</summary>
    public ApplicationRootSource RootSource { get; init; } =
        ApplicationRootSource.Explicit;

    /// <summary>The install root; every other member lives beneath it.</summary>
    public string RootDirectory => DataDirectory;

    public string SettingsFile =>
        Path.Combine(ConfigDirectory, "settings.json");

    public string KeyBindingsFile =>
        Path.Combine(ConfigDirectory, "keybinds.json");

    /// <summary>Client versions, the current-version records and the update lock.</summary>
    public string AppDirectory =>
        Path.Combine(DataDirectory, "app");

    /// <summary>The prepared game content and its install record.</summary>
    public string GameDataDirectory =>
        Path.Combine(DataDirectory, "data");

    public string LogsDirectory =>
        Path.Combine(DataDirectory, "logs");

    /// <summary>Client and launcher crash reports.</summary>
    public string CrashReportsDirectory =>
        Path.Combine(LogsDirectory, "crash");

    /// <summary>One folder per launcher-started session: its config, status and error log.</summary>
    public string LauncherSessionsDirectory =>
        Path.Combine(LogsDirectory, "launcher");

    public string ScreenshotsDirectory =>
        Path.Combine(DataDirectory, "screenshots");

    /// <summary>The player's quest journal notes.</summary>
    public string JournalDirectory =>
        Path.Combine(DataDirectory, "journal");

    /// <summary>Installed plugin code, one folder per plugin id.</summary>
    public string PluginsDirectory =>
        Path.Combine(DataDirectory, "plugins");

    /// <summary>
    /// The root plugin private storage is keyed under: a plugin's own files
    /// live at <c>&lt;id&gt;/files</c> beneath it, inside the plugin's folder,
    /// where installs, updates and code removal never touch them.
    /// </summary>
    public string PluginStorageDirectory => PluginsDirectory;

    /// <summary>Shared VTank-compatible profiles.</summary>
    public string VtankProfilesDirectory =>
        Path.Combine(DataDirectory, "vtank");

    public string DiagnosticsDirectory =>
        Path.Combine(CacheDirectory, "diagnostics");

    /// <summary>Where the clients on this machine leave notes for one another.</summary>
    public string PluginPeersDirectory =>
        Path.Combine(CacheDirectory, "plugin-peers");

    /// <summary>The private files folder of one plugin.</summary>
    public string PluginFilesDirectory(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return Path.Combine(
            PluginStorageDirectory,
            pluginId,
            PluginFilesFolderName);
    }

    /// <summary>The standard member layout of one root.</summary>
    public static ApplicationPathSet ForRoot(
        string rootDirectory,
        ApplicationRootSource source = ApplicationRootSource.Explicit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        string root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootDirectory));
        return new ApplicationPathSet(
            Path.Combine(root, "settings"),
            root,
            Path.Combine(root, "cache"))
        {
            RootSource = source,
        };
    }

    /// <summary>
    /// The per-user default root: <c>%LOCALAPPDATA%\OpenAC</c> on Windows,
    /// <c>~/Library/Application Support/OpenAC</c> on macOS and
    /// <c>$XDG_DATA_HOME/openac</c> (or <c>~/.local/share/openac</c>) on Linux.
    /// It is the only place the pointer file is read from.
    /// </summary>
    public static string ResolveDefaultRoot(
        IApplicationPathEnvironment? platform = null)
    {
        platform ??= ApplicationPathEnvironment.Instance;
        string root;
        if (platform.IsWindows)
        {
            root = Path.Combine(
                RequireFolder(
                    platform,
                    Environment.SpecialFolder.LocalApplicationData),
                "OpenAC");
        }
        else if (platform.IsMacOS)
        {
            root = Path.Combine(
                RequireFolder(platform, Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "OpenAC");
        }
        else
        {
            root = ResolveXdg(
                platform,
                "XDG_DATA_HOME",
                Path.Combine(
                    RequireFolder(
                        platform,
                        Environment.SpecialFolder.UserProfile),
                    ".local",
                    "share"),
                "openac");
        }

        return Normalize(root, platform);
    }

    /// <summary>
    /// Resolves every member. For each member the first of these that
    /// answers wins: an explicit member argument; an explicit root argument;
    /// the member's own environment variable; <c>ACDREAM_ROOT_DIR</c>; the
    /// pointer file in the default root; the default root.
    /// </summary>
    public static ApplicationPathSet Resolve(
        string? configDirectory = null,
        string? dataDirectory = null,
        string? cacheDirectory = null,
        IApplicationPathEnvironment? platform = null,
        string? rootDirectory = null)
    {
        platform ??= ApplicationPathEnvironment.Instance;

        configDirectory = NonEmpty(configDirectory);
        dataDirectory = NonEmpty(dataDirectory);
        cacheDirectory = NonEmpty(cacheDirectory);
        rootDirectory = NonEmpty(rootDirectory);
        bool anyExplicit = configDirectory is not null
            || dataDirectory is not null
            || cacheDirectory is not null
            || rootDirectory is not null;

        // An explicit root isolates the whole set from the environment: a
        // bot configuration or a launcher argument naming a folder means
        // exactly that folder, whatever the shell happened to export.
        if (rootDirectory is null)
        {
            configDirectory ??= NonEmpty(
                platform.GetEnvironmentVariable(ConfigEnvironmentVariable));
            dataDirectory ??= NonEmpty(
                platform.GetEnvironmentVariable(DataEnvironmentVariable));
            cacheDirectory ??= NonEmpty(
                platform.GetEnvironmentVariable(CacheEnvironmentVariable));
        }

        ApplicationRootSource source;
        string root;
        if (rootDirectory is not null)
        {
            root = rootDirectory;
            source = ApplicationRootSource.Explicit;
        }
        else if (NonEmpty(platform.GetEnvironmentVariable(
                     RootEnvironmentVariable)) is { } environmentRoot)
        {
            root = environmentRoot;
            source = ApplicationRootSource.Environment;
        }
        else
        {
            string defaultRoot = ResolveDefaultRoot(platform);
            if (ApplicationRootPointer.TryRead(defaultRoot, platform)
                is { } pointed)
            {
                root = pointed;
                source = ApplicationRootSource.Pointer;
            }
            else
            {
                root = defaultRoot;
                source = ApplicationRootSource.Default;
            }
        }

        // A member named on its own moves that member off the chosen root,
        // so the set is no longer purely the pointer's or the default's.
        if ((configDirectory is not null
                || dataDirectory is not null
                || cacheDirectory is not null)
            && source is ApplicationRootSource.Pointer
                or ApplicationRootSource.Default)
        {
            source = anyExplicit
                ? ApplicationRootSource.Explicit
                : ApplicationRootSource.Environment;
        }

        string normalizedRoot = Normalize(root, platform);
        return new ApplicationPathSet(
            Normalize(
                configDirectory ?? Path.Combine(normalizedRoot, "settings"),
                platform),
            Normalize(dataDirectory ?? normalizedRoot, platform),
            Normalize(
                cacheDirectory ?? Path.Combine(normalizedRoot, "cache"),
                platform))
        {
            RootSource = source,
        };
    }

    internal static string ResolveXdg(
        IApplicationPathEnvironment platform,
        string variable,
        string fallback,
        string leaf)
    {
        string? configured = platform.GetEnvironmentVariable(variable);
        string root = string.IsNullOrWhiteSpace(configured)
            ? fallback
            : configured;
        return Path.Combine(root, leaf);
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    internal static string RequireFolder(
        IApplicationPathEnvironment platform,
        Environment.SpecialFolder folder)
    {
        string value = platform.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"The operating system did not provide {folder}.");
        }

        return value;
    }

    internal static string Normalize(
        string path,
        IApplicationPathEnvironment platform)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "Application directories cannot be empty.",
                nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(path, platform.CurrentDirectory));
    }
}
