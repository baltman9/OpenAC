using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class LauncherProfileDocument
{
    [JsonRequired]
    public int Version { get; set; } = LauncherProfileStore.CurrentVersion;

    public List<ServerProfile> Servers { get; set; } = [];

    // Null identifies documents created before the shared user list was introduced.
    public List<LauncherUser>? Users { get; set; }

    /// <summary>Offers a beta-only plugin in Discover and Add from URL. Omitted
    /// when false, its default, so an off document stays byte-identical for an older launcher; on,
    /// one refuses it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ShowBetaPlugins { get; set; }
}

public sealed record LauncherUser(string Account, string Password);
