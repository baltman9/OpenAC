using System.Text.Json.Serialization;

namespace AcDream.Launcher.Core.Profiles;

public sealed class AccountProfile
{
    [JsonRequired]
    public string Account { get; set; } = string.Empty;

    [JsonRequired]
    public string Password { get; set; } = string.Empty;

    public List<CharacterProfile> Characters { get; set; } = [];

    /// <summary>The character this account's row is set to launch, or null for the character screen.
    /// Left out of the file while unset, so an older launcher can still read the profile.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? SelectedCharacter { get; set; }

    /// <summary>The launch mode this account's row is set to. Left out of the file while unset.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public LaunchMode? SelectedLaunchMode { get; set; }
}
