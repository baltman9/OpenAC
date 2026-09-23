using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcDream.Platform;

/// <summary>
/// The pointer file <c>root.json</c> in the default root:
/// <c>{ "version": 1, "root": "&lt;absolute path&gt;" }</c>. It exists only
/// when the player moved the install somewhere else, and it is the only thing
/// left in the default root afterwards, so a client or windowless host started
/// by hand still finds the moved install.
/// </summary>
public static class ApplicationRootPointer
{
    /// <summary>The pointer file's name in the default root.</summary>
    public const string FileName = "root.json";

    private const int CurrentVersion = 1;

    /// <summary>The pointer file's path for a default root.</summary>
    public static string PathFor(string defaultRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultRoot);
        return Path.Combine(defaultRoot, FileName);
    }

    /// <summary>
    /// The root the pointer names, or null when there is no pointer. A pointer
    /// that exists but cannot be understood is an error rather than silence:
    /// falling back to the default would make every setting and plugin file
    /// look lost.
    /// </summary>
    /// <exception cref="InvalidOperationException">The pointer is unreadable.</exception>
    public static string? TryRead(
        string defaultRoot,
        IApplicationPathEnvironment platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        string path = PathFor(defaultRoot);
        string? text = platform.ReadTextFile(path);
        if (text is null)
            return null;

        return Parse(text)
            ?? throw new InvalidOperationException(
                $"The install folder pointer '{path}' is not valid. It must be "
                + "{ \"version\": 1, \"root\": \"<absolute folder>\" }. "
                + "Fix it, or delete it to use the default folder.");
    }

    /// <summary>The root named by pointer text, or null when the text is not a valid pointer.</summary>
    public static string? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root
                || root["version"] is not JsonValue version
                || !version.TryGetValue(out int versionNumber)
                || versionNumber != CurrentVersion
                || root["root"] is not JsonValue rootValue
                || !rootValue.TryGetValue(out string? folder)
                || string.IsNullOrWhiteSpace(folder)
                || !Path.IsPathFullyQualified(folder))
            {
                return null;
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Points the default root at <paramref name="root"/>. Pointing it at
    /// itself removes the pointer instead, since the default needs none.
    /// </summary>
    public static void Write(string defaultRoot, string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        string fullDefault = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(defaultRoot));
        string fullRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(root));
        string path = PathFor(fullDefault);
        if (ApplicationPathIdentity.Equals(fullDefault, fullRoot))
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        Directory.CreateDirectory(fullDefault);
        var document = new JsonObject
        {
            ["version"] = CurrentVersion,
            ["root"] = fullRoot,
        };
        string temporary = path + ".tmp";
        File.WriteAllText(
            temporary,
            document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Folder identity with the platform's own case rule.</summary>
public static class ApplicationPathIdentity
{
    /// <summary>Whether two folders are the same, ignoring a trailing separator.</summary>
    public static bool Equals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="folder"/> or lies beneath it.</summary>
    public static bool IsSameOrInside(string candidate, string folder)
    {
        string fullFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        string fullCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        StringComparison comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(fullCandidate, fullFolder, comparison)
            || fullCandidate.StartsWith(
                fullFolder + Path.DirectorySeparatorChar,
                comparison);
    }
}
