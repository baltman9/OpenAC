using AcDream.Runtime.Plugins;

namespace AcDream.Headless.Configuration;

/// <summary>
/// The startup settings each plugin is given on a windowless session: the
/// ones the session document names, or the ones a file named by a startup
/// option holds.
/// </summary>
/// <remarks>
/// Both clients take these settings from the same two places and in the same
/// order, because that is the whole point of them: a plugin that decides what
/// to do on login from a setting must read the same setting whichever client
/// is running it. The session document is the more specific instruction, so
/// what it names outranks the startup option and leaves the file unread; a
/// session that names none falls back to the file.
///
/// A named file that is missing, unreadable or the wrong shape stops the
/// session with the reason, in the same words a badly written settings map in
/// the document stops it with. Coming up empty instead would give a plugin a
/// different startup on one client than on the other, silently.
/// </remarks>
internal static class HeadlessPluginSettingsOptions
{
    internal const string EnvironmentVariable = "ACDREAM_PLUGIN_SETTINGS_FILE";

    internal static PluginSessionSettings Resolve(
        IReadOnlyDictionary<string, Dictionary<string, string>>? declared) =>
        Resolve(declared, Environment.GetEnvironmentVariable);

    internal static PluginSessionSettings Resolve(
        IReadOnlyDictionary<string, Dictionary<string, string>>? declared,
        Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (declared is not null)
            return PluginSessionSettings.FromDeclared(declared);
        string? path = env(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(path)
            ? PluginSessionSettings.Empty
            : PluginSessionSettings.ReadFile(path);
    }
}
