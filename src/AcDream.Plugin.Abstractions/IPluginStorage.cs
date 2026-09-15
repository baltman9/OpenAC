namespace AcDream.Plugin.Abstractions;

public interface IPluginStorage
{
    bool IsAvailable => false;

    /// <summary>
    /// The absolute directory keys are written beneath, or null when this
    /// storage is not backed by files. Useful for telling a user where their
    /// data went; keys still go through this interface.
    /// </summary>
    string? RootPath => null;

    string? ReadText(string key) => null;
    /// <summary>Relative file keys beneath one relative prefix.</summary>
    IReadOnlyList<string> List(string prefix) => Array.Empty<string>();
    void WriteText(string key, string content) =>
        throw new NotSupportedException("Plugin storage is unavailable.");
    bool Delete(string key) => false;
}

public sealed class NoOpPluginStorage : IPluginStorage
{
    public static NoOpPluginStorage Instance { get; } = new();
    private NoOpPluginStorage() { }
}
