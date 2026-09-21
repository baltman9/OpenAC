using System.Reflection;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The state view is the quietest place the two clients can disagree: every
/// member of <see cref="IGameState"/> has an empty default, so a client that
/// answers nothing looks exactly like a client with nothing to say. This
/// census reads which members each client's own state type really answers and
/// makes the difference either absent or written down with a reason.
/// </summary>
public sealed class PluginStateCensusTests
{
    /// <summary>The state type each client really hands its plugins.</summary>
    private static Type StateTypeFor(string host) =>
        host == ParityHost.Windowed
            ? typeof(AcDream.Core.Plugins.WorldGameState)
            : typeof(AcDream.Headless.Plugins.HeadlessPluginHost);

    /// <summary>
    /// Whether this type answers the member itself, rather than falling
    /// through to the interface's empty default.
    /// </summary>
    private static bool Answers(Type stateType, PropertyInfo member)
    {
        MethodInfo getter = member.GetGetMethod()
            ?? throw new InvalidOperationException(
                $"{member.Name} has no getter to map.");
        InterfaceMapping map = stateType.GetInterfaceMap(typeof(IGameState));
        for (int index = 0; index < map.InterfaceMethods.Length; index++)
        {
            if (map.InterfaceMethods[index] != getter)
                continue;
            return map.TargetMethods[index] != getter;
        }

        return false;
    }

    private static IEnumerable<PropertyInfo> StateMembers() =>
        typeof(IGameState)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(static member => member.Name, StringComparer.Ordinal);

    [Fact]
    public void EveryStateMemberIsAnsweredByBothClientsOrWrittenDown()
    {
        var unlisted = new List<string>();
        foreach (PropertyInfo member in StateMembers())
        {
            foreach (string host in ParityHost.Both)
            {
                if (Answers(StateTypeFor(host), member))
                    continue;
                bool listed = HostParityAllowList.StateMembers.Any(entry =>
                    entry.Member == member.Name && entry.MissingHost == host);
                if (!listed)
                    unlisted.Add($"{member.Name} on the {host} client");
            }
        }

        Assert.True(
            unlisted.Count == 0,
            "A plugin reads these and gets an empty list on one client with "
            + "nothing saying so: " + string.Join(", ", unlisted));
    }

    [Fact]
    public void NoStateMemberIsWrittenDownThatBothClientsAnswer()
    {
        string[] stale = HostParityAllowList.StateMembers
            .Where(entry => StateMembers()
                .Where(member => member.Name == entry.Member)
                .Any(member => Answers(StateTypeFor(entry.MissingHost), member)))
            .Select(static entry => $"{entry.Member} on the {entry.MissingHost} client")
            .ToArray();

        Assert.True(
            stale.Length == 0,
            "These are written down as a difference and are not one any more: "
            + string.Join(", ", stale));
    }
}
