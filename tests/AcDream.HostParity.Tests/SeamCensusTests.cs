using System.Reflection;
using AcDream.App.Plugins;
using AcDream.Headless.Plugins;
using AcDream.Runtime;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The gate: everything a plugin can reach is supplied by both hosts, or it
/// sits in the allow-list with a reason and the stage that closes it.
///
/// The census reads each host's standing declaration rather than starting
/// the host, because one of them cannot exist without a window. The
/// declarations cannot quietly drift: the shared binding pass refuses a
/// capability record that supplies anything its host has not declared, so
/// any run of either host -- including every host test -- proves the
/// automation declarations against the record the host really builds.
///
/// Mutation check (2026-09-20): removing the session-commands capability
/// from the windowless host's declared set turned
/// <see cref="EverySurfaceSeamIsSuppliedByBothHostsOrAllowListed"/> red with
/// "BindSessionCommands is missing on windowless"; restoring it turned the
/// test green again.
/// </summary>
public sealed class SeamCensusTests
{
    /// <summary>
    /// Every seam on the shared plugin surface, including the optional
    /// arguments a host can leave out on its own.
    /// </summary>
    private static IReadOnlyList<string> AllSeams()
    {
        var seams = new List<string>();
        foreach (MethodInfo method in typeof(RuntimeAutomationSurface)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(static method =>
                method.Name.StartsWith("Bind", StringComparison.Ordinal))
            .OrderBy(static method => method.Name, StringComparer.Ordinal))
        {
            seams.Add(method.Name);
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (parameter.IsOptional)
                    seams.Add($"{method.Name}.{parameter.Name}");
            }
        }
        return seams;
    }

    private static IReadOnlySet<string> DeclaredCapabilities(string host) =>
        host == ParityHost.Windowed
            ? GraphicalAutomationCapabilities.Declared
            : HeadlessAutomationCapabilities.Declared;

    private static IReadOnlySet<string> SeamsFor(string host)
    {
        IReadOnlySet<string> declared = DeclaredCapabilities(host);
        return RuntimeAutomationBindings.SeamCapabilities
            .Where(entry => entry.Value is null || declared.Contains(entry.Value))
            .Select(static entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    [Fact]
    public void EverySurfaceSeamIsMappedToACapabilityThatExists()
    {
        IReadOnlyList<string> seams = AllSeams();

        string[] unmapped = seams
            .Where(static seam =>
                !RuntimeAutomationBindings.SeamCapabilities.ContainsKey(seam))
            .ToArray();
        Assert.True(
            unmapped.Length == 0,
            "The shared plugin surface has seams the binding pass knows "
            + "nothing about, so no host can be shown to fill them: "
            + string.Join(", ", unmapped));

        string[] phantom = RuntimeAutomationBindings.SeamCapabilities.Keys
            .Where(seam => !seams.Contains(seam))
            .ToArray();
        Assert.True(
            phantom.Length == 0,
            "The binding pass maps seams the surface no longer has: "
            + string.Join(", ", phantom));

        string[] unknownCapability = RuntimeAutomationBindings.SeamCapabilities
            .Values
            .Where(static capability => capability is not null
                && !RuntimeAutomationHostCapabilities.AllCapabilityNames
                    .Contains(capability))
            .Select(static capability => capability!)
            .ToArray();
        Assert.True(
            unknownCapability.Length == 0,
            "The binding pass needs capabilities a host cannot express: "
            + string.Join(", ", unknownCapability));
    }

    [Fact]
    public void EveryDeclaredCapabilityIsOneTheSurfaceCanBeGiven()
    {
        foreach (string host in ParityHost.Both)
        {
            string[] unknown = DeclaredCapabilities(host)
                .Where(static name => !RuntimeAutomationHostCapabilities
                    .AllCapabilityNames.Contains(name))
                .ToArray();
            Assert.True(
                unknown.Length == 0,
                $"The {host} host declares capabilities that do not exist: "
                + string.Join(", ", unknown));
        }
    }

    [Fact]
    public void EverySurfaceSeamIsSuppliedByBothHostsOrAllowListed() =>
        AssertParity(
            AllSeams(),
            SeamsFor,
            HostParityAllowList.Seams,
            "plugin surface seam",
            missingOnBothIsADifference: true);

    [Fact]
    public void EveryRuntimeDependencyIsSuppliedByBothHostsOrAllowListed() =>
        AssertParity(
            MembersOf(typeof(GameRuntimeDependencies)),
            static host => host == ParityHost.Windowed
                ? GraphicalAutomationCapabilities.DeclaredRuntimeDependencies
                : HeadlessAutomationCapabilities.DeclaredRuntimeDependencies,
            HostParityAllowList.RuntimeDependencies,
            "runtime dependency",
            // A member neither host fills in is the same default on both,
            // which is parity rather than a difference.
            missingOnBothIsADifference: false);

    [Fact]
    public void EverySessionHostBindingIsSuppliedByBothHostsOrAllowListed() =>
        AssertParity(
            MembersOf(typeof(LiveSessionHostBindings))
                .Concat(MembersOf(typeof(LiveSessionSelectionBindings)))
                .Concat(MembersOf(typeof(LiveSessionEnteredWorldBindings)))
                .ToArray(),
            static host => host == ParityHost.Windowed
                ? GraphicalAutomationCapabilities.DeclaredSessionHostBindings
                : HeadlessAutomationCapabilities.DeclaredSessionHostBindings,
            HostParityAllowList.SessionHostBindings,
            "live-session host binding",
            missingOnBothIsADifference: false);

    [Fact]
    public void EveryCharacterSessionBindingIsSuppliedByBothHostsOrAllowListed() =>
        AssertParity(
            MembersOf(typeof(LiveCharacterSessionBindings)),
            static host => host == ParityHost.Windowed
                ? GraphicalAutomationCapabilities.DeclaredCharacterSessionBindings
                : HeadlessAutomationCapabilities.DeclaredCharacterSessionBindings,
            HostParityAllowList.CharacterSessionBindings,
            "character-session binding",
            missingOnBothIsADifference: false);

    private static IReadOnlyList<string> MembersOf(Type record) => record
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(static property => property.Name != "EqualityContract")
        .Select(static property => property.Name)
        .OrderBy(static name => name, StringComparer.Ordinal)
        .ToArray();

    /// <summary>
    /// Compares what the two hosts supply, member by member, against the
    /// allow-list. Fails on an unlisted difference, on a listed entry that no
    /// longer describes one, and -- for the plugin surface -- on a seam
    /// neither host fills, which is a seam nothing can reach.
    /// </summary>
    private static void AssertParity(
        IReadOnlyList<string> members,
        Func<string, IReadOnlySet<string>> suppliedBy,
        IReadOnlyList<ParityAllowance> allowList,
        string what,
        bool missingOnBothIsADifference)
    {
        string[] unknownMember = allowList
            .Where(entry => !members.Contains(entry.Member))
            .Select(static entry => entry.Member)
            .ToArray();
        Assert.True(
            unknownMember.Length == 0,
            $"The allow-list names a {what} that does not exist: "
            + string.Join(", ", unknownMember));

        string[] unknownHost = allowList
            .Where(static entry => !ParityHost.Both.Contains(entry.MissingHost))
            .Select(static entry => entry.MissingHost)
            .ToArray();
        Assert.True(
            unknownHost.Length == 0,
            "The allow-list names hosts that do not exist: "
            + string.Join(", ", unknownHost));

        Assert.All(allowList, entry => Assert.False(
            string.IsNullOrWhiteSpace(entry.Reason),
            $"The allow-list entry for {entry.Member} has no reason."));

        var allowed = allowList
            .Select(static entry => (entry.Member, entry.MissingHost))
            .ToHashSet();
        var unlisted = new List<string>();
        var stale = new List<string>();

        foreach (string member in members)
        {
            bool missingOnBoth = ParityHost.Both.All(
                host => !suppliedBy(host).Contains(member));
            foreach (string host in ParityHost.Both)
            {
                bool supplied = suppliedBy(host).Contains(member);
                bool listed = allowed.Contains((member, host));
                bool isADifference = !supplied
                    && (missingOnBothIsADifference || !missingOnBoth);
                if (isADifference && !listed)
                    unlisted.Add($"{member} is missing on {host}");
                else if (!isADifference && listed)
                    stale.Add($"{member} on {host}");
            }
        }

        Assert.True(
            unlisted.Count == 0,
            $"The hosts disagree about a {what} and nothing says why. Supply "
            + "it on both hosts, or add an allow-list entry with a reason and "
            + "the stage that closes it: " + string.Join("; ", unlisted));
        Assert.True(
            stale.Count == 0,
            $"The allow-list still excuses a {what} that is no longer a "
            + "difference. Delete the entry: " + string.Join("; ", stale));
    }
}
