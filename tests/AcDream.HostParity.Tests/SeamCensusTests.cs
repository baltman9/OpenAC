using System.Reflection;
using AcDream.App.Plugins;
using AcDream.Headless.Plugins;
using AcDream.Runtime;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Session;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The gate: everything a plugin can reach is supplied by both hosts, or it
/// sits in the allow-list, either as a debt with the stage that closes it or
/// as a difference inherent to one client drawing the world and the other not.
///
/// Where it can, the census OBSERVES rather than reads a list: each host's
/// capability record and dependency record are built here by the host's own
/// builder, and its declaration has to equal what that builder produced. A
/// member dropped from a host's real construction therefore turns the census
/// red instead of leaving a list behind that says otherwise.
///
/// Convention: a seam is a <c>Bind*</c> method on the shared surface, and the
/// census discovers them by that name. A host can also push INTO the surface
/// under another verb; those entry points are invisible to the discovery and
/// are named in <see cref="HostPushedEntryPoints"/> instead.
///
/// A binding that is there and does nothing is read as absent: see
/// <see cref="InertBindings"/>. Until that rule existed a host could satisfy
/// every claim in this file with an empty lambda, which is what the windowless
/// host did for arriving in the world.
///
/// Mutation checks (2026-09-20):
/// * removing the session-commands capability from the windowless host's
///   declared set turned
///   <see cref="EverySurfaceSeamIsSuppliedByBothHostsOrAllowListed"/> red with
///   "BindSessionCommands is missing on windowless";
/// * dropping <c>Log:</c> from the windowed host's real dependency
///   construction turned
///   <see cref="EachHostDeclaresExactlyWhatItsDependencyRecordSupplies"/> red
///   with "windowed declares a runtime dependency its own construction does
///   not supply: Log";
/// * dropping <c>AnswerConfirmation</c> from the windowless host's real
///   capability construction turned
///   <see cref="EachHostDeclaresExactlyWhatItsCapabilityRecordSupplies"/> red.
/// Restoring each turned the test green again.
/// </summary>
public sealed class SeamCensusTests
{
    /// <summary>
    /// Host-to-surface entry points that are not <c>Bind*</c> seams, so the
    /// discovery below cannot find them. Each one is a place a host pushes
    /// something in, and each has to reach plugins on both hosts.
    /// </summary>
    private static readonly string[] HostPushedEntryPoints =
        [nameof(RuntimeAutomationSurface.RaiseConfirmationRequested)];

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

    /// <summary>
    /// Every family of standing declaration the census compares, with what
    /// each host says it fills in and which of those it can only fill in
    /// under a condition. A family left out of this table is one where a
    /// condition can be named and nothing checks it.
    /// </summary>
    private static readonly (
        string What,
        Func<string, IReadOnlySet<string>> Declared,
        Func<string, IReadOnlyDictionary<string, string>> Conditional)[]
        DeclarationFamilies =
        [
            ("plugin capability",
                DeclaredCapabilities,
                ObservedHostRecords.ConditionalCapabilities),
            ("runtime dependency",
                DeclaredRuntimeDependencies,
                ObservedHostRecords.ConditionalRuntimeDependencies),
            ("live-session host binding",
                DeclaredSessionHostBindings,
                ObservedHostRecords.ConditionalSessionHostBindings),
            ("character-session binding",
                DeclaredCharacterSessionBindings,
                ObservedHostRecords.ConditionalCharacterSessionBindings),
        ];

    private static IReadOnlySet<string> DeclaredSessionHostBindings(
        string host) =>
        host == ParityHost.Windowed
            ? GraphicalAutomationCapabilities.DeclaredSessionHostBindings
            : HeadlessAutomationCapabilities.DeclaredSessionHostBindings;

    private static IReadOnlySet<string> DeclaredCharacterSessionBindings(
        string host) =>
        host == ParityHost.Windowed
            ? GraphicalAutomationCapabilities.DeclaredCharacterSessionBindings
            : HeadlessAutomationCapabilities.DeclaredCharacterSessionBindings;

    private static IReadOnlySet<string> DeclaredCapabilities(string host) =>
        host == ParityHost.Windowed
            ? GraphicalAutomationCapabilities.Declared
            : HeadlessAutomationCapabilities.Declared;

    private static IReadOnlySet<string> DeclaredRuntimeDependencies(string host) =>
        host == ParityHost.Windowed
            ? GraphicalAutomationCapabilities.DeclaredRuntimeDependencies
            : HeadlessAutomationCapabilities.DeclaredRuntimeDependencies;

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

    /// <summary>
    /// The declaration has to be what the host's own builder produces, not a
    /// list beside it. Without this a member deleted from the real record
    /// leaves the census green and a plugin holding nothing.
    /// </summary>
    [Fact]
    public void EachHostDeclaresExactlyWhatItsCapabilityRecordSupplies()
    {
        foreach (string host in ParityHost.Both)
        {
            IReadOnlySet<string> supplied =
                ObservedHostRecords.SuppliedCapabilities(
                    ObservedHostRecords.CapabilitiesFor(host));
            IReadOnlySet<string> declared = DeclaredCapabilities(host);
            AssertSameMembers(
                declared,
                supplied,
                host,
                "plugin capability",
                "the record it builds with every part present");
        }
    }

    /// <summary>The same, for the record each host builds its runtime with.</summary>
    [Fact]
    public void EachHostDeclaresExactlyWhatItsDependencyRecordSupplies()
    {
        foreach (string host in ParityHost.Both)
        {
            IReadOnlySet<string> supplied = ObservedHostRecords.SuppliedMembers(
                ObservedHostRecords.RuntimeDependenciesFor(host));
            IReadOnlySet<string> declared = DeclaredRuntimeDependencies(host);
            AssertSameMembers(
                declared,
                supplied,
                host,
                "runtime dependency",
                "the record it builds the runtime with");
        }
    }

    /// <summary>
    /// The same, for the bindings each host hands the live-session host. This
    /// is the record that carried the longest-lived hand-written claim in the
    /// census, and the one where a host can hand over a binding that does
    /// nothing at all.
    /// </summary>
    [Fact]
    public void EachHostDeclaresExactlyWhatItsSessionHostBindingsSupply()
    {
        foreach (string host in ParityHost.Both)
        {
            AssertSameMembers(
                host == ParityHost.Windowed
                    ? GraphicalAutomationCapabilities.DeclaredSessionHostBindings
                    : HeadlessAutomationCapabilities.DeclaredSessionHostBindings,
                ObservedHostRecords.SuppliedSessionHostMembersFor(host),
                host,
                "live-session host binding",
                "the record it builds the session host with");
        }
    }

    /// <summary>The same, for the bindings that follow one character.</summary>
    [Fact]
    public void EachHostDeclaresExactlyWhatItsCharacterSessionBindingsSupply()
    {
        foreach (string host in ParityHost.Both)
        {
            AssertSameMembers(
                host == ParityHost.Windowed
                    ? GraphicalAutomationCapabilities
                        .DeclaredCharacterSessionBindings
                    : HeadlessAutomationCapabilities
                        .DeclaredCharacterSessionBindings,
                ObservedHostRecords.SuppliedMembers(
                    ObservedHostRecords.CharacterSessionBindingsFor(host)),
                host,
                "character-session binding",
                "the record it builds the event router with");
        }
    }

    /// <summary>
    /// A condition names something the host also declares, and says why in
    /// words a reader can act on. Every family of declaration is checked:
    /// capabilities, runtime dependencies and both halves of the
    /// live-session bindings.
    /// </summary>
    [Fact]
    public void EveryNamedConditionBelongsToADeclaredMember()
    {
        foreach (string host in ParityHost.Both)
        {
            foreach ((string what,
                    Func<string, IReadOnlySet<string>> declared,
                    Func<string, IReadOnlyDictionary<string, string>>
                        conditional) in DeclarationFamilies)
            {
                AssertConditionsAreDeclared(
                    conditional(host),
                    declared(host),
                    host,
                    what);
            }
        }
    }

    /// <summary>
    /// Something one host supplies always and the other only sometimes is a
    /// difference a plugin meets in a particular session, so it is listed
    /// like any other. Again over every family: a binding that arrives only
    /// with installed content is as real a difference as a capability that
    /// does.
    /// </summary>
    [Fact]
    public void AConditionalMemberTheOtherHostAlwaysSuppliesIsAllowListed()
    {
        var allowed = HostParityAllowList.ConditionalSeams
            .Select(static entry => (entry.Member, entry.ConditionalHost))
            .ToHashSet();
        Assert.All(HostParityAllowList.ConditionalSeams, entry =>
        {
            Assert.Contains(entry.ConditionalHost, ParityHost.Both);
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        });

        var unlisted = new List<string>();
        var listedAndReal = new HashSet<(string, string)>();
        foreach (string host in ParityHost.Both)
        {
            string other = host == ParityHost.Windowed
                ? ParityHost.Windowless
                : ParityHost.Windowed;
            foreach ((string what,
                    Func<string, IReadOnlySet<string>> declared,
                    Func<string, IReadOnlyDictionary<string, string>>
                        conditional) in DeclarationFamilies)
            {
                foreach (string member in declared(host))
                {
                    bool conditionalHere =
                        conditional(host).ContainsKey(member);
                    bool alwaysThere = declared(other).Contains(member)
                        && !conditional(other).ContainsKey(member);
                    if (!conditionalHere || !alwaysThere)
                        continue;
                    if (allowed.Contains((member, host)))
                        listedAndReal.Add((member, host));
                    else
                        unlisted.Add($"{what} {member} is conditional only on {host}");
                }
            }
        }

        string[] stale = allowed
            .Where(entry => !listedAndReal.Contains(entry))
            .Select(static entry => $"{entry.Item1} on {entry.Item2}")
            .OrderBy(static line => line, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unlisted.Count == 0,
            "One host can only sometimes supply what the other always "
            + "supplies, and nothing says why: " + string.Join("; ", unlisted));
        Assert.True(
            stale.Length == 0,
            "The allow-list still excuses a condition that is no longer a "
            + "difference. Delete the entry: " + string.Join("; ", stale));
    }

    /// <summary>
    /// The seams the census discovers by name are not all of them: a host can
    /// push into the surface under another verb. Those are named explicitly so
    /// the convention is visible rather than silently incomplete.
    /// </summary>
    [Fact]
    public void EveryNamedHostPushedEntryPointStillExists()
    {
        foreach (string entryPoint in HostPushedEntryPoints)
        {
            Assert.True(
                typeof(RuntimeAutomationSurface).GetMethod(
                    entryPoint,
                    BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance) is not null,
                $"The census names a host-pushed entry point the surface no "
                + $"longer has: {entryPoint}");
            Assert.False(
                entryPoint.StartsWith("Bind", StringComparison.Ordinal),
                $"{entryPoint} is an ordinary seam and the census finds it on "
                + "its own; it does not belong in the host-pushed list.");
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
            DeclaredRuntimeDependencies,
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

    /// <summary>
    /// The same, for what each host hands the surface at construction. These
    /// are the inputs no later binding can supply: a host that builds its
    /// surface without them leaves part of the plugin API dead for the whole
    /// run, which is exactly what the windowless host did with the tick the
    /// surface announces this client on.
    /// </summary>
    [Fact]
    public void EachHostDeclaresExactlyWhatItsSurfaceInputsSupply()
    {
        foreach (string host in ParityHost.Both)
        {
            AssertSameMembers(
                host == ParityHost.Windowed
                    ? GraphicalAutomationCapabilities.DeclaredSurfaceInputs
                    : HeadlessAutomationCapabilities.DeclaredSurfaceInputs,
                ObservedHostRecords.SuppliedSurfaceInputsFor(host),
                host,
                "plugin surface input",
                "the record it builds the surface from");
        }
    }

    /// <summary>
    /// An input one host passes and the other does not is a difference, and
    /// one neither passes is a surface nothing can announce itself from, so
    /// both fail here.
    /// </summary>
    [Fact]
    public void EverySurfaceInputIsSuppliedByBothHostsOrAllowListed() =>
        AssertParity(
            RuntimeAutomationSurfaceInputs.AllInputNames
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray(),
            ObservedHostRecords.SuppliedSurfaceInputsFor,
            HostParityAllowList.SurfaceInputs,
            "plugin surface input",
            missingOnBothIsADifference: true);

    /// <summary>
    /// Every listed difference names a host that exists and says why in words
    /// a reader can act on, and the list uses both of its kinds. A list with
    /// no permanent exceptions has written up every drawn-world difference as
    /// work someone is going to do, which is how it came to overstate what
    /// was left.
    /// </summary>
    [Fact]
    public void EveryListedDifferenceNamesAKnownHostAndAReason()
    {
        Assert.All(HostParityAllowList.All, entry =>
        {
            Assert.Contains(entry.MissingHost, ParityHost.Both);
            Assert.False(string.IsNullOrWhiteSpace(entry.Member));
            Assert.False(
                string.IsNullOrWhiteSpace(entry.Reason),
                $"The entry for {entry.Member} on {entry.MissingHost} says "
                + "nothing about why the hosts differ.");
        });

        Assert.Contains(HostParityAllowList.All, static e => e.IsDebt);
        Assert.Contains(HostParityAllowList.All, static e => !e.IsDebt);
    }

    private static void AssertConditionsAreDeclared(
        IReadOnlyDictionary<string, string> conditions,
        IReadOnlySet<string> declared,
        string host,
        string what)
    {
        foreach ((string member, string condition) in conditions)
        {
            Assert.True(
                declared.Contains(member),
                $"The {host} host names a condition for a {what} it does not "
                + $"declare at all: {member}");
            Assert.False(
                string.IsNullOrWhiteSpace(condition),
                $"The {host} host's condition for {member} says nothing.");
        }
    }

    private static void AssertSameMembers(
        IReadOnlySet<string> declared,
        IReadOnlySet<string> supplied,
        string host,
        string what,
        string sourceDescription)
    {
        string[] declaredOnly = declared
            .Where(name => !supplied.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        string[] suppliedOnly = supplied
            .Where(name => !declared.Contains(name))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            declaredOnly.Length == 0,
            $"The {host} host declares a {what} its own construction does not "
            + $"supply, so the census promises plugins something {sourceDescription} "
            + $"never produces: {string.Join(", ", declaredOnly)}");
        Assert.True(
            suppliedOnly.Length == 0,
            $"The {host} host supplies a {what} it does not declare, so the "
            + "census cannot see it: " + string.Join(", ", suppliedOnly));
    }

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
            + "it on both hosts, or add an allow-list entry: a debt with the "
            + "stage that closes it, or a difference inherent to drawing: "
            + string.Join("; ", unlisted));
        Assert.True(
            stale.Count == 0,
            $"The allow-list still excuses a {what} that is no longer a "
            + "difference. Delete the entry: " + string.Join("; ", stale));
    }
}
