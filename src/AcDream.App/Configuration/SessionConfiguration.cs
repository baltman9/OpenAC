using System.Text.Json.Serialization;

namespace AcDream.App.Configuration;

internal sealed class SessionConfiguration
{
    [JsonRequired]
    public int Version { get; init; }

    public SessionProcessSettings? Process { get; init; }

    [JsonRequired]
    public List<SessionDescriptor?> Sessions { get; init; } = [];
}

internal sealed class SessionProcessSettings
{
    public SessionContentDescriptor? Content { get; init; }

    public SessionProcessPathOverrides? Paths { get; init; }
}

internal sealed class SessionProcessPathOverrides
{
    public string? ConfigDirectory { get; init; }
    public string? DataDirectory { get; init; }
    public string? CacheDirectory { get; init; }
}

internal sealed class SessionContentDescriptor
{
    [JsonRequired]
    public string DatDirectory { get; init; } = string.Empty;

    [JsonRequired]
    public string PreparedAssetPath { get; init; } = string.Empty;

    public string? PreparedAssetOverlayPath { get; init; }

    public uint? PreparedAssetBaseRecipeVersion { get; init; }

    public uint? PreparedAssetEffectiveRecipeVersion { get; init; }
}

internal sealed record SessionDescriptor
{
    [JsonRequired]
    public string Id { get; init; } = string.Empty;

    [JsonRequired]
    public SessionEndpointDescriptor Endpoint { get; init; } = new();

    [JsonRequired]
    public string Account { get; init; } = string.Empty;

    public SessionCharacterSelectorDescriptor? Character { get; init; }

    public SessionPolicyDescriptor? Policy { get; init; }

    public string? Mode { get; init; }

    [JsonRequired]
    public SessionCredentialDescriptor Credential { get; init; } = new();

    /// <summary>
    /// The character options this session should arrive with. Both clients
    /// check the map against the same declarable set and seed it the same way
    /// on login, so one document means the same thing whichever starts it.
    /// </summary>
    public Dictionary<string, bool>? CharacterOptions { get; init; }

    /// <summary>Plugin ids to load. Absent = load all; explicit
    /// empty = load none.</summary>
    public List<string>? Plugins { get; init; }

    /// <summary>
    /// The words this session wants to be found by. Plugins on the clients
    /// running on this machine can see one another's tags and filter on them,
    /// so a bot that should look like part of a group carries the group's
    /// word here. When the document leaves this out, the client falls back to
    /// the word list its own launch options name.
    /// </summary>
    public List<string>? PluginTags { get; init; }

    /// <summary>
    /// The startup settings each plugin was given, keyed by plugin id and
    /// then by setting name. Both clients hand the same map to the same
    /// per-plugin settings reader, so a plugin reads the same settings
    /// whichever client started from this document.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>>? PluginSettings { get; init; }

    public List<string>? LoginCommands { get; init; }

    public int LoginCommandDelayMs { get; init; } = 500;

    public string? StatusFile { get; init; }
}

internal sealed class SessionEndpointDescriptor
{
    [JsonRequired]
    public string Host { get; init; } = string.Empty;

    [JsonRequired]
    public int Port { get; init; }
}

internal sealed class SessionCharacterSelectorDescriptor
{
    public int? Index { get; init; }
    public uint? Id { get; init; }
    public string? Name { get; init; }
}

/// <summary>Loose by design: the windowed client never inspects the policy's
/// shape beyond "does this document parse" — <c>Id</c>/<c>Role</c> stay
/// untyped strings so this DTO never has to track the windowless client's own
/// policy-id/role vocabulary.</summary>
internal sealed class SessionPolicyDescriptor
{
    public string? Id { get; init; }
    public string? Role { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<SessionCredentialProviderKind>))]
internal enum SessionCredentialProviderKind
{
    Environment,
    StandardInput,
    File,
}

internal sealed class SessionCredentialDescriptor
{
    [JsonRequired]
    public SessionCredentialProviderKind Provider { get; init; }

    [JsonRequired]
    public string Reference { get; init; } = string.Empty;
}
