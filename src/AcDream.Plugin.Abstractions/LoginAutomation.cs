namespace AcDream.Plugin.Abstractions;

public readonly record struct PluginLoginCharacter(
    uint ObjectId,
    string Name,
    int ActiveIndex,
    bool IsPendingDelete);

public interface ILoginAutomation
{
    bool IsAvailable => false;
    uint NextLoginObjectId => 0u;

    IReadOnlyList<PluginLoginCharacter> CaptureRoster() =>
        Array.Empty<PluginLoginCharacter>();

    bool SetNextLogin(uint characterObjectId) => false;
    bool ClearNextLogin() => false;

    /// <summary>
    /// The client's own graceful logout: the same route the UI's logout
    /// control uses. Returns false when there is no in-world session to log
    /// out of.
    /// </summary>
    bool Logout() => false;
}
