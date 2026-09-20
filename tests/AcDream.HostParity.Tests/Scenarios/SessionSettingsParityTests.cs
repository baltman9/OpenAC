using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// What a plugin reads out of the settings its run was started with. Both
/// clients are configured with the same map -- one from a launch option
/// naming a file, the other from its session file -- so a plugin that decides
/// what to do on login from a setting decides the same thing on either.
///
/// It is read the way a plugin really reads it: through the per-plugin
/// wrapper each client puts around its host, which is what turns "the whole
/// map" into "this plugin's own settings".
///
/// Mutation check (2026-09-20), run: taking the settings away from the
/// windowed arm's host -- which is what that client did before it had the
/// option at all -- turned
/// <see cref="APluginReadsTheSameStartupSettingsOnBothClients"/> red on the
/// first value it read. Restoring it turned it green.
/// </summary>
public sealed class SessionSettingsParityTests
{
    [Fact]
    public void APluginReadsTheSameStartupSettingsOnBothClients() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            transcript.Step("the settings this plugin was started with");
            Record(transcript, arm, ParityArm.SettingsPluginId, "startMacro");
            Record(transcript, arm, ParityArm.SettingsPluginId, "profile");

            transcript.Step("another plugin's settings are its own");
            Record(
                transcript, arm, ParityArm.OtherSettingsPluginId, "startMacro");
            Record(transcript, arm, ParityArm.OtherSettingsPluginId, "profile");

            transcript.Step("a plugin nobody configured");
            Record(transcript, arm, "acdream.parity.unconfigured", "startMacro");
        });

    /// <summary>
    /// Reads one setting the way a plugin does: through the wrapper its host
    /// is behind, which hands a plugin only its own.
    /// </summary>
    private static void Record(
        ParityTranscript transcript,
        ParityArm arm,
        string pluginId,
        string key)
    {
        using var scope = new ScopedPluginHost(arm.Host, pluginId, pluginId);
        IReadOnlyDictionary<string, string> settings = scope.SessionSettings;
        transcript.Record($"{pluginId}.count", settings.Count);
        transcript.Record(
            $"{pluginId}.{key}",
            settings.TryGetValue(key, out string? value) ? value : "<absent>");
    }
}
