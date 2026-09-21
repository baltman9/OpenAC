using System.Reflection;
using AcDream.App.Plugins;
using AcDream.Headless.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The startup settings a plugin reads are the quietest input a client can
/// drop: the plugin API answers an empty map by default, so a client that
/// supplies nothing looks exactly like a run that was configured with
/// nothing. This census reads what each client's own plugin host really does
/// with them.
///
/// Mutation checks (2026-09-20), each run:
/// * taking <see cref="IPerPluginSessionSettings"/> off the windowed host
///   turned <see cref="BothClientsAnswerAPluginItsOwnStartupSettings"/> red;
/// * dropping the settings argument from the windowed host's constructor
///   turned <see cref="BothClientsTakeTheirStartupSettingsAtConstruction"/>
///   red;
/// * giving the windowed host a copy of its own to answer out of turned
///   <see cref="BothClientsAnswerOutOfTheOneSharedSnapshot"/> red.
/// Restoring each turned them green again.
/// </summary>
public sealed class PluginHostSessionSettingsCensusTests
{
    /// <summary>The plugin host each client really hands its plugins.</summary>
    private static Type HostTypeFor(string host) =>
        host == ParityHost.Windowed
            ? typeof(AppPluginHost)
            : typeof(HeadlessPluginHost);

    /// <summary>
    /// A host that does not implement this answers every plugin the API's
    /// empty default, whatever the run was configured with.
    /// </summary>
    [Fact]
    public void BothClientsAnswerAPluginItsOwnStartupSettings()
    {
        foreach (string host in ParityHost.Both)
        {
            Assert.True(
                typeof(IPerPluginSessionSettings).IsAssignableFrom(
                    HostTypeFor(host)),
                $"The {host} client does not answer a plugin its own startup "
                + "settings, so every plugin reads an empty map there however "
                + "the run was configured.");
        }
    }

    /// <summary>
    /// And it has to be able to receive them. A host that implements the
    /// interface over a set nothing can fill answers empty for the whole run,
    /// which is the difference this census exists to catch.
    /// </summary>
    [Fact]
    public void BothClientsTakeTheirStartupSettingsAtConstruction()
    {
        foreach (string host in ParityHost.Both)
        {
            Assert.True(
                HostTypeFor(host)
                    .GetConstructors(
                        BindingFlags.Public | BindingFlags.NonPublic
                            | BindingFlags.Instance)
                    .SelectMany(static constructor => constructor.GetParameters())
                    .Any(static parameter =>
                        parameter.ParameterType == typeof(PluginSessionSettings)),
                $"The {host} client cannot be handed the startup settings a "
                + "run was configured with, so its plugins read an empty map "
                + "whatever the configuration says.");
        }
    }

    /// <summary>
    /// One implementation, not two that agree today: both clients answer out
    /// of the same snapshot type, so the copying, the lookup and the answer
    /// for a plugin nobody configured cannot drift apart.
    /// </summary>
    [Fact]
    public void BothClientsAnswerOutOfTheOneSharedSnapshot()
    {
        foreach (string host in ParityHost.Both)
        {
            Assert.True(
                HostTypeFor(host)
                    .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                    .Any(static field =>
                        field.FieldType == typeof(PluginSessionSettings)),
                $"The {host} client keeps its own copy of the startup settings "
                + "rather than the shared snapshot both clients answer from.");
        }
    }
}
