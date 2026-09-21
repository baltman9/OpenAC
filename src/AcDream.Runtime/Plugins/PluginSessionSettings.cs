using System.Text.Json;
using AcDream.Plugin.Abstractions;

namespace AcDream.Runtime.Plugins;

/// <summary>
/// Thrown when the per-plugin startup settings a client was started with
/// cannot be used: the file naming them is missing or unreadable, or the map
/// itself has a hole in it. A client raises this instead of starting with an
/// empty set, so a plugin never quietly runs without the settings the player
/// asked for.
/// </summary>
public sealed class PluginSessionSettingsException : Exception
{
    /// <summary>A fault with nothing behind it.</summary>
    /// <param name="message">What is wrong, in words the player can act on.</param>
    public PluginSessionSettingsException(string message)
        : base(message)
    {
    }

    /// <summary>A fault raised while handling another one.</summary>
    /// <param name="message">What is wrong, in words the player can act on.</param>
    /// <param name="innerException">The fault underneath it.</param>
    public PluginSessionSettingsException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The startup settings a client was given for each plugin, keyed by plugin
/// id and then by setting name -- the profile a bot should apply on login,
/// say. One implementation for both clients: each reads the same map out of
/// its own startup configuration and answers a plugin from this snapshot, so
/// a plugin that reads its settings behaves the same however the client was
/// started.
///
/// The map is copied at construction, so a caller that goes on editing the
/// dictionary it handed in cannot change what a plugin later reads.
/// </summary>
public sealed class PluginSessionSettings : IPerPluginSessionSettings
{
    private static readonly IReadOnlyDictionary<string, string> NoSettings =
        new Dictionary<string, string>(StringComparer.Ordinal);

    // The file holds exactly the map and nothing else: a name we do not know
    // is a plugin that is not installed rather than a misspelling we could
    // catch, so there is no member list to be strict about. Everything else
    // is as strict as the other configuration a client reads.
    private static readonly JsonSerializerOptions FileOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>
        _byPlugin;

    private PluginSessionSettings(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byPlugin) =>
        _byPlugin = byPlugin;

    /// <summary>No settings for anyone, which is what an unconfigured client has.</summary>
    public static PluginSessionSettings Empty { get; } =
        new(new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.Ordinal));

    /// <summary>The plugin ids this client was given settings for.</summary>
    public IReadOnlyCollection<string> PluginIds => _byPlugin.Keys.ToArray();

    /// <summary>
    /// Takes a copy of a declared map, after checking it has no holes in it.
    /// </summary>
    /// <param name="declared">
    /// Plugin id to that plugin's settings; null or empty means none.
    /// </param>
    /// <exception cref="PluginSessionSettingsException">
    /// A plugin's settings, or one of its values, is null.
    /// </exception>
    public static PluginSessionSettings FromDeclared(
        IReadOnlyDictionary<string, Dictionary<string, string>>? declared)
    {
        if (DescribeFault(declared) is { } fault)
            throw new PluginSessionSettingsException(fault);
        return Snapshot(declared);
    }

    /// <summary>
    /// Reads the map out of a JSON file whose whole content is the map: one
    /// object of settings per plugin id.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <exception cref="PluginSessionSettingsException">
    /// The file is missing, unreadable, not that shape, or has a hole in it.
    /// </exception>
    public static PluginSessionSettings ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception error)
            when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PluginSessionSettingsException(
                $"Plugin settings file '{path}' does not exist.",
                error);
        }
        catch (Exception error)
            when (error is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            throw new PluginSessionSettingsException(
                $"Plugin settings file '{path}' could not be read: "
                + error.Message,
                error);
        }

        Dictionary<string, Dictionary<string, string>>? declared;
        try
        {
            declared = JsonSerializer
                .Deserialize<Dictionary<string, Dictionary<string, string>>>(
                    text,
                    FileOptions);
        }
        catch (JsonException error)
        {
            throw new PluginSessionSettingsException(
                $"Plugin settings file '{path}' is not valid JSON: "
                + error.Message,
                error);
        }

        if (declared is null)
        {
            throw new PluginSessionSettingsException(
                $"Plugin settings file '{path}' must hold an object mapping "
                + "plugin ids to their settings.");
        }

        if (DescribeFault(declared) is { } fault)
        {
            throw new PluginSessionSettingsException(
                $"Plugin settings file '{path}': {fault}");
        }

        return Snapshot(declared);
    }

    /// <summary>
    /// What is wrong with a declared map, in the words every client reports it
    /// with, or null when there is nothing wrong. Read by a client that wants
    /// to report the fault against its own configuration by name.
    /// </summary>
    /// <param name="declared">The map to look over.</param>
    public static string? DescribeFault(
        IReadOnlyDictionary<string, Dictionary<string, string>>? declared)
    {
        if (declared is null)
            return null;

        foreach ((string pluginId, Dictionary<string, string>? perPlugin)
            in declared)
        {
            if (perPlugin is null)
                return $"pluginSettings['{pluginId}'] cannot be null.";

            foreach ((string key, string? value) in perPlugin)
            {
                if (value is null)
                {
                    return
                        $"pluginSettings['{pluginId}']['{key}'] cannot be null.";
                }
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> SessionSettingsFor(string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        return _byPlugin.TryGetValue(
            pluginId,
            out IReadOnlyDictionary<string, string>? settings)
            ? settings
            : NoSettings;
    }

    private static PluginSessionSettings Snapshot(
        IReadOnlyDictionary<string, Dictionary<string, string>>? declared)
    {
        if (declared is null || declared.Count == 0)
            return Empty;

        var copy = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            declared.Count,
            StringComparer.Ordinal);
        foreach ((string pluginId, Dictionary<string, string>? perPlugin)
            in declared)
        {
            copy[pluginId] = perPlugin is { Count: > 0 }
                ? new Dictionary<string, string>(perPlugin, StringComparer.Ordinal)
                : NoSettings;
        }
        return new PluginSessionSettings(copy);
    }
}
